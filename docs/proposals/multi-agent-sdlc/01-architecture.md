## Multi-agent SDLC workflows — architecture

**Status:** proposal · **Audience:** Archer maintainers · **See also:** [README](./README.md), [02-yaml-schema](./02-yaml-schema.md), [03-sdlc-example](./03-sdlc-example.md), [04-research](./04-research.md), [05-roadmap](./05-roadmap.md)

### Goals

1. Compose multiple agents into a workflow that produces SDLC artifacts (PRD, technical design, test plan, security review) end-to-end.
2. Three first-class collaboration modes:
   - **solo** — one agent drafts an artifact (existing single-agent loop).
   - **critic** — a primary agent's artifact is scored against rubrics by N critic agents; the primary revises until convergence.
   - **peer** (a.k.a. *swarm*) — N agents share one conversation, see each other's messages, negotiate to convergence on a single artifact.
3. A **workspace** that can span multiple repositories so an architect can read backend + frontend + infra repos in one workflow.
4. Workflows defined declaratively in YAML, hot-reloadable like agent definitions.
5. Reuse existing pieces: `IArcherAgentGrain`, `IModelTurnRunner`, `IToolRegistry`, `IAgentEventSink`, MCP tools, persistence, OTel tracing.

### Non-goals (for the first slice)

- Cross-machine distribution of workflows. Orleans handles this for free; we don't need to design for it.
- Visual workflow editor. YAML + TUI text view is enough.
- General-purpose orchestration. The shape is biased toward SDLC; we won't try to be Airflow.
- Human-in-the-loop checkpoints. Designed-for, not built-in-v1.
- Conditional / branching phases. Will land later (see [roadmap](./05-roadmap.md)).

### Big picture

```
                                ┌────────────────────────────────────┐
                                │       WorkflowDefinition (YAML)    │
                                │  (workflows/sdlc-feature.yaml)     │
                                └────────────────┬───────────────────┘
                                                 │ hot-reloaded by
                                                 ▼
                                ┌────────────────────────────────────┐
                                │   IWorkflowDefinitionRegistry      │
                                │   (mirrors AgentDefinitionRegistry)│
                                └────────────────┬───────────────────┘
                                                 │
                                                 ▼
            ┌────────────────────────────────────────────────────────────────┐
            │                        IWorkflowGrain                          │
            │  primary key: workflow_<id>                                    │
            │  state: { activePhaseIdx, phaseStates, artifacts, workspace } │
            │                                                                │
            │   advances phases  ──>  schedules child grains  <── events    │
            │      │                            │                            │
            │      ▼                            ▼                            │
            │  ┌──────────────────┐     ┌──────────────────────┐             │
            │  │ ISoloPhaseGrain  │     │  IPeerChatGrain      │             │
            │  │ (1 primary)      │     │  (N peers, shared)   │             │
            │  └────────┬─────────┘     └────────┬─────────────┘             │
            │           │                         │                          │
            │           └────────┬────────────────┘                          │
            │                    ▼                                           │
            │           ┌─────────────────────┐                              │
            │           │ IArcherAgentGrain   │  (existing — unchanged)      │
            │           └─────────────────────┘                              │
            └────────────────────────────────────────────────────────────────┘
                                                 │
                                                 ▼
                                ┌────────────────────────────────────┐
                                │  IWorkspaceStore                   │
                                │  artifacts/{workflowId}/PRD.md     │
                                │  artifacts/{workflowId}/DESIGN.md  │
                                │  events/{workflowId}.ndjson        │
                                └────────────────────────────────────┘
```

The WorkflowGrain owns the *control plane* (which phase, who's involved, what artifacts exist). The phase grains own the *data plane* (the actual conversations and artifact updates). Existing `IArcherAgentGrain` is unchanged — the new grains drive it from above.

### Why a new grain layer?

Today the contract is *one user → one agent → one conversation*. Multi-agent workflows break that contract in three ways:

1. **The "user" of an inner agent is another agent.** Agent A's output becomes B's prompt — but A wasn't running an interactive turn, it was producing an artifact.
2. **A peer-mode phase has N agents on one conversation.** No single agent grain owns it; the conversation lives between them.
3. **Workflow state outlives any individual turn.** A workflow can pause mid-phase (waiting for a tool, a human, or a long-running review), be reloaded after process restart, and resume.

Trying to bolt these onto `IArcherAgentGrain` would make agents bidirectional message buses, conflate orchestration with reasoning, and lose Orleans' clean activation boundary. Hence: a new grain layer that *uses* agents instead of *being* one.

### Workflow grain

```csharp
[Alias("Archer.IWorkflowGrain")]
public interface IWorkflowGrain : IGrainWithStringKey
{
    Task<WorkflowSnapshot> StartAsync(NewWorkflowRequest request);
    Task<WorkflowSnapshot?> GetSnapshotAsync();
    Task InterruptAsync(InterruptRequest request);
    Task RetryPhaseAsync(string phaseId);
    Task ResumeAsync();                  // after host restart
}
```

Primary key: `workflow_<id>` where `<id>` is generated like `agent_…`. State persisted via `IWorkflowStateStore` (a sibling of `IAgentStateStore`).

`NewWorkflowRequest` carries:
- `WorkflowDefinitionId` — the YAML id (e.g. `sdlc-feature`)
- `WorkspaceConfig` — concrete repos for this run
- `Inputs` — initial values for declared inputs (e.g. `requirement: "We need a webhooks service…"`)
- `OperatorAgentId` — optional; the human's terminal in the chat

The grain advances through the phase array stored in the definition. Each phase is dispatched to one of:
- `ISoloPhaseGrain` — for `mode: solo` and `mode: critic` (critic *is* solo + a feedback sub-loop)
- `IPeerChatGrain` — for `mode: peer`

When a child grain reports completion, the workflow grain validates the produced artifact, advances `activePhaseIdx`, and schedules the next.

### Phase model

A **phase** is a unit of progress that:
- has an `id` unique within the workflow
- declares its `mode` (`solo` / `critic` / `peer`)
- declares which **agents** participate (1 primary for solo/critic, N for peer)
- declares its **inputs** (text values + prior artifacts)
- declares its **artifacts** (what it must produce or revise)
- declares a **completion** predicate (artifact written? sign-off received? max rounds hit?)

Phases compose linearly today; future versions add fan-out and conditionals.

#### Solo mode

The simplest case. The workflow grain spawns an agent grain for the primary, populates its system prompt with the phase's instructions, presents the inputs as the first user message, and runs a turn. Existing `TurnWorkerGrain` does the work. The phase completes when the agent declares the artifact written (via a new `write_artifact` tool — see [§ Artifact tool](#artifact-tool)).

```
Workflow ──spawn(product-owner)──> Agent ─turn─> tools[write_artifact("prd", body)]
   ▲                                                              │
   └──────── ArtifactWritten event ◄────────────────────────────┘
   ▼
   advance to next phase
```

#### Critic mode

Same primary as solo, plus N **critic** agents that each evaluate the artifact independently against a rubric. After the primary writes a draft:

1. Workflow fans out to each critic in parallel (each is a separate solo agent grain run, with the artifact + rubric as input).
2. Each critic returns a structured `CriticReport` (rubric scores + comments + verdict: pass/fail).
3. Reports are aggregated and given back to the primary.
4. Primary either revises (writes a new version of the artifact) or signs off.
5. Loop until all critics pass OR `max-rounds` hit OR primary explicitly overrides.

The critics never see each other's reports during a round — they're independent evaluators, not collaborators. This keeps the design simple and the LLM context small.

#### Peer mode (swarm)

Multiple agents share one conversation. Implemented by a new `IPeerChatGrain`:

```csharp
[Alias("Archer.IPeerChatGrain")]
public interface IPeerChatGrain : IGrainWithStringKey
{
    Task StartAsync(PeerChatStartRequest request);
    Task<PeerChatSnapshot> GetSnapshotAsync();
    // Internal — invoked by next-speaker selector:
    [Alias("AdvanceTurn")] Task<PeerTurnOutcome> AdvanceTurnAsync();
}
```

State:
- the **shared message log** — every peer agent's outputs are appended here in order
- the **artifact** under joint authorship
- `roundIndex`, `lastSpeaker`, `signOffs` (who has explicitly approved the current revision)

Each agent participates by being invoked with a system prompt that includes:
- its own role description (from its `AgentDefinition`)
- the phase's role-specific guidance ("you are reviewing for security vulnerabilities")
- the full shared transcript
- the current artifact

Turn-taking is decided by a **next-speaker selector**:
- v1: round-robin (predictable, debuggable)
- v2: LLM-based selector that reads the last message and picks who should respond
- v3: agents can address each other directly ("@security-lead, what's your take on the token storage?") and the selector honours @-mentions

Convergence options (see also [§ Convergence](#convergence)):
- `sign-off:agent` — wait until that agent says "approved" (parsed from message + tool call)
- `consensus` — all peers signed off
- `max-rounds:N` — hard cap; on hit, hand to operator (or chosen tie-breaker)

Note on Orleans-fit: each `AdvanceTurn` is a single grain method invocation. The grain is single-threaded so there's no race on the shared log. Agent-to-agent calls go *through* the peer-chat grain — agent A doesn't message agent B directly; it speaks into the shared log, the peer-chat grain decides whose turn is next, and that next agent reads the log fresh on activation.

### Artifact model

Artifacts are the deliverables: PRD, technical design, test plan, security review. Each has:

```csharp
public sealed record Artifact
{
    public required string Name { get; init; }       // "prd"
    public required string Path { get; init; }       // "docs/PRD.md" (relative to workspace.artifactRoot)
    public required ArtifactFormat Format { get; init; }  // Markdown, Json, Yaml, Code
    public required string Body { get; init; }
    public required string AuthorAgentId { get; init; }
    public required Guid PhaseRunId { get; init; }
    public required int Revision { get; init; }      // bumped on each rewrite
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
```

Storage: `IWorkspaceStore` persists artifacts under `<workspace>/.archer/workflows/<workflowId>/artifacts/<name>.v<n>.md`, plus a `current` symlink/pointer. Versioning is intrinsic — every revision is kept so peer-review history is audit-able.

#### Artifact tool

Agents write artifacts through a new tool, registered the same way as `list_files` etc.:

```jsonc
{
  "name": "write_artifact",
  "parameters": {
    "name":   "string (required, must match a declared artifact in this phase)",
    "body":   "string (required, full new content; we don't do partial patches in v1)",
    "summary":"string (one-line change description, surfaced in events)"
  }
}
```

It's gated: a phase declares which artifacts it can write/revise; the tool refuses writes to undeclared names. The `IWorkflowGrain` is the source of truth for what's writeable when.

A complementary `read_artifact` tool exposes prior artifacts (e.g. the architect reads the PRD).

A `comment_on_artifact` tool lets a critic post structured feedback without rewriting the body — used in critic mode to avoid critics overwriting each other.

### Workspace and multi-repo

Today `IRepoPathResolver` assumes a single root. The workflow grain operates in a workspace that may contain multiple repos:

```yaml
workspace:
  default-repo: backend
  repos:
    - id: backend
      path: ./services/backend
    - id: frontend
      path: ./apps/web
    - id: infra
      path: ../platform/infra-repo
  artifact-root: ./docs/workflows  # PRD/DESIGN land here, not in any repo
```

Tools that resolve files (list_files, grep, search_pattern, write_artifact, etc.) accept an optional `repo` argument. When omitted, they fall back to `default-repo`. The existing `IRepoPathResolver` becomes `IWorkspacePathResolver` — backwards-compatible because a single-repo workspace is just a special case.

```csharp
public interface IWorkspacePathResolver
{
    bool TryResolve(WorkspaceContext ws, string repoOrNull, string relativePath,
                    out string fullPath, out string? error);
}
```

The workspace context flows through `ToolRequest` so each tool call knows which workspace it's in:

```csharp
public sealed record ToolRequest(
    string ToolCallId,
    string ToolName,
    JsonObject Arguments,
    string RepoRoot,                  // legacy single-repo, still populated for back-compat
    string AgentId,
    WorkspaceContext? Workspace = null);  // new — when running under a workflow
```

`WorkspaceContext` carries the repo map and artifact root; tools that need it pull it out, single-agent uses keep working unchanged.

### Convergence

A handful of named strategies, all expressible in YAML:

| Strategy | Predicate | Use |
|----------|-----------|-----|
| `artifact-written:<name>` | The named artifact has at least one revision in this phase. | Solo mode default. |
| `all-critics-pass` | Every critic returned `verdict: pass`. | Critic mode (with `max-rounds` fallback). |
| `sign-off:<agent-id>` | That specific peer used the `sign_off` tool with `approved: true`. | Peer mode default. |
| `consensus` | Every peer signed off on the current revision. | Strong-agreement peer mode. |
| `max-rounds:<N>` | Round counter hit. | Always combined with one of the above as a fallback. |
| `custom:<predicate-id>` | Plugin point — a registered C# predicate evaluates the snapshot. | Future. |

The composite is `(primary OR fallback) AND (artifact-written:<name> OR allow-empty)` — i.e. a phase always needs at least one artifact revision unless the YAML explicitly waives that.

### Events and observability

Every step emits structured events through `IAgentEventSink` — the same channel today's single agent uses, with new event types:

```csharp
public sealed record WorkflowStartedEvent : AgentEvent { /* … */ }
public sealed record PhaseStartedEvent : AgentEvent
{
    public required string PhaseId;
    public required PhaseMode Mode;
    public required IReadOnlyList<string> ParticipatingAgents;
}
public sealed record ArtifactWrittenEvent : AgentEvent
{
    public required string ArtifactName;
    public required int Revision;
    public required string AuthorAgentId;
    public required string Summary;
}
public sealed record CriticReportEvent : AgentEvent
{
    public required string ArtifactName;
    public required string CriticAgentId;
    public required IReadOnlyDictionary<string, double> Scores;
    public required string Verdict;       // "pass" | "fail" | "needs-revision"
    public required string Comments;
}
public sealed record PeerTurnEvent : AgentEvent
{
    public required string SpeakerAgentId;
    public required int RoundIndex;
}
public sealed record SignOffEvent : AgentEvent
{
    public required string PhaseId;
    public required string AgentId;
    public required int ArtifactRevision;
}
public sealed record PhaseCompletedEvent : AgentEvent { /* … */ }
public sealed record WorkflowCompletedEvent : AgentEvent { /* … */ }
```

`AgentId` on the base event becomes the *workflow* id when the event is workflow-level; the participating agent ids carry on the typed payload. The TUI subscribes to the same sink and renders a workflow tab differently — see `04-tui.md` (future).

OTel tracing nests naturally:
```
trace: archer.workflow
  span: archer.phase (phaseId, mode)
    span: archer.peer-turn (speaker, round)  -- only in peer mode
      span: archer.turn      (existing)
        span: archer.model.call
        span: archer.tool.<name>
```

### Persistence and resumption

Workflow state is durable:

```csharp
public sealed class WorkflowState
{
    public required string WorkflowId;
    public required string DefinitionId;
    public required WorkspaceConfig Workspace;
    public required int ActivePhaseIndex;
    public required IReadOnlyList<PhaseRunState> PhaseStates;
    public required IReadOnlyList<ArtifactSummary> Artifacts;  // names + current revision
    public required DateTimeOffset CreatedAtUtc;
    public required DateTimeOffset UpdatedAtUtc;
    public WorkflowStatus Status;  // Running | Paused | Failed | Completed | Interrupted
}
```

`IWorkflowStateStore` persists this. Default impl mirrors `FileAgentStateStore` — JSON state file, NDJSON event log, artifacts on disk under the workspace's artifact root.

After a host restart, the workflow grain rehydrates state, inspects `ActivePhaseIndex` and `PhaseStates[active].Status`, and resumes:
- if the active phase was mid-turn for an agent, the agent grain resumes its turn (already supported)
- if the active phase was mid-peer-chat, the peer-chat grain rehydrates from the shared log and asks the selector who's next
- if the workflow was waiting on a critic fan-out, it re-fires only the critics that hadn't reported

### Where things plug in

| New | Location | Existing analog |
|-----|----------|------------------|
| `WorkflowDefinition` (record) | `Archer.Domain.Workflows` | `AgentDefinition` |
| `IWorkflowDefinitionRegistry` | `Archer.Application.Workflows` | `IAgentDefinitionRegistry` |
| `YamlWorkflowDefinitionLoader` | `Archer.Persistence.Workflows` | `YamlAgentDefinitionLoader` |
| `IWorkflowStateStore`, `FileWorkflowStateStore` | `Archer.Persistence` | `IAgentStateStore` |
| `IWorkflowGrain`, `WorkflowGrain` | `Archer.Actors` | `IArcherAgentGrain` |
| `ISoloPhaseGrain`, `SoloPhaseGrain` | `Archer.Actors` | `ITurnWorkerGrain` |
| `IPeerChatGrain`, `PeerChatGrain` | `Archer.Actors` | new |
| `WorkspaceContext`, `IWorkspacePathResolver` | `Archer.Domain.Workspaces` + `Archer.Tools.Safety` | `RepoPathResolver` |
| `write_artifact`, `read_artifact`, `comment_on_artifact`, `sign_off` tools | `Archer.Tools` | `list_files` etc. |
| TUI workflow tab | `Archer.Tui.Ui` | `AgentTabView` |
| `archer workflow run/status/resume` CLI | `Archer.Cli.Commands` | `archer new` |

Existing components — agent grain, turn worker, model runner, MCP, tools — are **not modified** beyond receiving an optional `WorkspaceContext` in `ToolRequest`. The point of this design is to compose them into something bigger, not rewrite them.

### Phases as a DAG (planned for v2, designed-for in v1)

The schema in [02-yaml-schema](./02-yaml-schema.md) ships with phases as a linear array, because that's enough for the SDLC walkthrough and avoids the cognitive load of a graph editor up front. **But the runtime is built around a DAG model, with linear chains as the trivial special case.** Each phase carries an explicit `next-phases` field (defaulting to "the next item in the array") so v2 can introduce fan-out and conditionals without a redesign:

```yaml
phases:
  - id: discovery
    next-phases: [po-review]
  - id: po-review
    next-phases: [architecture]
  - id: architecture
    next-phases: [design-review, scaffold]    # parallel branches
  - id: design-review
    next-phases: [merge]
  - id: scaffold
    next-phases: [merge]
  - id: merge                                  # join — runs when all incoming are done
```

Why design this in from the start: ChatDev 1.0 shipped a strictly linear chain and the same team abandoned that for a graph runtime in 2.0 (`workflow/graph.py`, YAML-defined subgraphs in `yaml_instance/`). LangGraph and AutoGen's `SelectorGroupChat` both validate that branching, joining, and looping subgraphs are the natural shape of multi-agent orchestration. We adopt the lesson up-front. See [04-research](./04-research.md) for the full comparison.

v1 implementation:
- The workflow grain stores the phase array but treats `phaseStates` as a map keyed by phase id, not by array index.
- Advancement is "find phases whose `depends-on` are satisfied and not yet started; start them." For a linear array that reduces to "the next item." For a DAG it generalises naturally.
- Joins (multiple `next-phases` converging on one) wait for all upstream phases to complete before starting.

### Per-phase agent reset

Each phase starts its participating agents with **a fresh conversation history**. They don't carry context from earlier phases — that's what artifacts are for. The architect's input is the PRD artifact, not "everything the PO ever said." This is deliberate:

1. **Token cost** — without reset, every phase context grows unboundedly.
2. **Prompt poisoning** — failed turns or tool errors from phase A shouldn't pollute phase B.
3. **Determinism** — re-running a phase from saved inputs should produce the same behaviour.

ChatDev classic does this same reset (`camel/agents/role_playing.py:186-187` calls `assistant.reset()` and `user.reset()` per phase). We follow the same discipline.

Mechanically: the agent grain isn't *reset* in place — instead the workflow uses a fresh grain instance per phase, keyed `agent_<workflow-id>_<phase-id>_<role-id>`. When the phase advances, the prior phase's agent grains deactivate; their state is durable but nothing addresses them again.

### OS-as-tester (architectural note for future test phases)

A future Test phase (out of scope for v1) should follow ChatDev's pattern: don't have an LLM hallucinate test outcomes — run the actual test command via a tool, capture stdout/stderr, feed that text to a summariser agent. The phase becomes:

1. Programmer writes/revises code (artifact: branch diff or file set)
2. Tool call: `dotnet test ./solution.slnx` — captures real exit code + output
3. Tester agent reads tool output, summarises failures into structured feedback (rubric per failure category)
4. If failures, hand back to programmer; loop with `max-rounds` cap

General principle: when the *truth* of an artifact's correctness is checkable by an external system (compiler, linter, test runner, type checker), use that system as ground truth and reduce the agent's role to summarisation and remediation, not invention.

### Open architectural questions

These are deliberately deferred — the [roadmap](./05-roadmap.md) sequences them.

1. **Concurrency inside a phase.** Critics today fan out in parallel. Should peer mode allow parallel speakers? (Probably not — the value of swarm is shared context, which sequences naturally.)
2. **Selector pluggability.** Round-robin v1 is debuggable but dumb. The LLM-based selector adds latency and a failure mode. Worth designing the interface to let both coexist.
3. **Long-running phases.** A peer review with 4 agents and 12 rounds of model calls could take many minutes. Orleans grains have an idle timeout. We'll need to either make the workflow grain `[KeepAlive]` or rely on grain reactivation (which it already supports via persisted state).
4. **Cost guardrails.** Multi-agent workflows multiply token cost by N. Need a budget / kill-switch tied to OTel metrics.
5. **Human-in-the-loop checkpoints.** Trivial extension (a phase with `mode: human` waits for a CLI/TUI sign-off), but the UX needs design.
6. **Artifact merge conflicts.** In peer mode multiple agents may try to write the same artifact in the same round. v1 enforces one writer per turn; the question is how to surface "rejected because someone else just wrote".

Continued in [02-yaml-schema](./02-yaml-schema.md), [03-sdlc-example](./03-sdlc-example.md), [04-research](./04-research.md), [05-roadmap](./05-roadmap.md).
