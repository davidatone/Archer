## Implementation roadmap

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [02-yaml-schema](./02-yaml-schema.md), [03-sdlc-example](./03-sdlc-example.md), [04-research](./04-research.md)

This document sequences the work in five phases. Each phase ships a usable slice. Each has explicit *done-when* criteria, dependencies, and the open questions it must resolve. Time estimates are deliberately wide ranges — multi-agent systems hit unknown unknowns.

The ordering is chosen so that the **first slice is testable end-to-end on a real workflow** (the SDLC example with critic mode but without peer mode). Peer/swarm — the conceptually hardest piece — lands in slice 3, by which point the surrounding plumbing is proven.

### Slice 1 — workflow runtime + solo phase (groundwork)

**Goal:** an operator can define and run a single-phase, single-agent workflow that produces an artifact in a multi-repo workspace. This slice has no critics or peers — it proves out workspace, artifacts, the workflow grain, and YAML.

**Deliverables:**

- `Archer.Domain.Workflows`: `WorkflowDefinition`, `PhaseDefinition`, `WorkspaceConfig`, `ArtifactDefinition`, mode + completion records.
- `Archer.Application.Workflows`: `IWorkflowDefinitionRegistry`, the artifact-tool contracts.
- `Archer.Persistence.Workflows`: `YamlWorkflowDefinitionLoader`, `IWorkflowStateStore` + `FileWorkflowStateStore`, `IWorkspaceStore` (artifact persistence under the workflow's state dir).
- `Archer.Actors`: `IWorkflowGrain` + `WorkflowGrain`, `ISoloPhaseGrain` + `SoloPhaseGrain`. The latter is thin — it spawns an agent grain and listens for the artifact-written event.
- `Archer.Tools`: `write_artifact` and `read_artifact` tools with workspace-aware path resolution. Existing tools (`list_files`, `grep`, `search_pattern`) accept an optional `repo` argument.
- `Archer.Tools.Safety`: `IWorkspacePathResolver` replacing `IRepoPathResolver` in workflow-aware tool paths. Existing single-repo `RepoPathResolver` becomes the legacy fallback.
- `Archer.Cli.Commands`: `archer workflow run <id>`, `archer workflow status <workflow-id>`, `archer workflow events <workflow-id> --follow`.
- Tests:
  - YAML loader tests (round-trip, error messages, hot reload).
  - WorkspaceContext + multi-repo path resolution.
  - WorkflowGrain integration test (Orleans TestCluster) running a one-phase solo workflow that writes an artifact.
  - `write_artifact` gating: refuses writes to undeclared artifacts; refuses writes outside the active phase.

**Done-when:**
1. `archer workflow run hello-world` produces a markdown file at the declared path.
2. `archer workflow status` shows the workflow as `Completed` with the artifact listed.
3. Workflow survives `archer workflow run` → kill the host → restart → `archer workflow status` returns the same final state.
4. The existing single-agent flows (`archer new`, `archer interactive`) keep working unchanged.

**Open questions to resolve in this slice:**
- **WorkspaceContext propagation.** Add it to `ToolRequest` (proposed). Does any existing code break? Audit `ITool` implementations for callers that assume `RepoRoot` is the only path source.
- **Artifact path conflict handling.** Two workflows in the same workspace writing to the same artifact relative path — what happens? Proposal: artifact root is keyed by workflow id, so paths are isolated by default.
- **Hot-reload semantics for in-flight workflows.** Already-running workflows keep their captured definition (snapshotted at start). New runs pick up the new YAML. Confirm with a test.

**Estimated effort:** medium. The Domain/Application/Persistence layers mirror existing agent code closely; the workflow grain itself is small (≈150-250 LOC). Mostly plumbing.

### Slice 2 — critic mode

**Goal:** rubric-based critic loops with parallel critic fan-out and structured `CriticReport` aggregation.

**Deliverables:**

- `Archer.Domain.Workflows`: `Rubric`, `RubricDimension`, `CriticReport`, `CriticAggregateResult`.
- `Archer.Persistence.Workflows`: `YamlRubricLoader`, `IRubricRegistry`. Hot-reload from `rubrics/*.yaml`.
- `Archer.Actors`: extend `SoloPhaseGrain` to handle `mode: critic` revision loops. Critic fan-out runs critics as one-shot solo agent grain invocations; the workflow grain awaits all reports before deciding next step.
- `Archer.Tools`: `comment_on_artifact` tool. Schema-validated against `CriticReport`; refuses output if scores are out of rubric range or required dimensions are missing.
- Events: `CriticReportEvent`, `CriticAggregateEvent`, `RevisionStartedEvent`.
- Tests:
  - Rubric loader (round-trip, validation).
  - Critic mode integration test: a primary writes an intentionally-flawed artifact; critics flag specific failures; primary revises; loop converges in 2 rounds.
  - Critic mode max-rounds fallback: critics never pass; phase exits with most-recent revision and a structured "did not converge" event.
  - Critic schema enforcement: a critic that returns malformed data triggers a re-prompt; if still malformed, the critic's report is recorded as `verdict: needs-revision` with a synthetic comment.

**Done-when:**
1. The SDLC example's `po-review` phase runs end-to-end: 4 critics evaluate a PRD, primary revises, critics re-evaluate, all pass.
2. Critics can be re-run on a saved artifact (`archer workflow critic-rerun <workflow-id> <phase-id>`) without re-running prior phases.
3. Critic reports are stored alongside artifacts and queryable via the events log.

**Open questions:**
- **Aggregation strategy.** Weighted average vs. require-unanimous-pass — both supported in YAML, but is there a third "strict gate" rule (every dimension above its `pass-at` threshold)? Proposal: yes, `verdict-gate: every-dimension-passes`.
- **Critic disagreement.** What if critic A says "pass" and critic B says "fail" with low confidence? v1 sums weighted scores; future v2 might add a meta-critic to resolve.
- **Cost control for critic fan-out.** Four critics × N rounds is up to 4N model calls. Proposal: emit an OTel metric `archer.workflow.critic-cost` so operators can track; add a per-phase token budget guard.

**Estimated effort:** medium. The fan-out + aggregate is conceptually clean; tool schema enforcement and the rubric registry are mechanical.

### Slice 3 — peer/swarm mode

**Goal:** N agents share a conversation, take turns via a selector, sign off via structured tool calls, and converge on an artifact.

**Deliverables:**

- `Archer.Domain.Workflows`: `PeerChatTranscript` (the shared message log), `SignOff` record, `SelectorChoice` enum.
- `Archer.Application.Workflows`: `INextSpeakerSelector` interface + three implementations: `RoundRobinSelector`, `MentionAwareSelector`, `LlmSelector`.
- `Archer.Actors`: `IPeerChatGrain` + `PeerChatGrain`. Owns the shared transcript; per-turn invokes the chosen peer with the full transcript + the agent's role-specific framing. Tracks per-revision sign-offs.
- `Archer.Tools`: `sign_off` tool — peer-only, schema-enforced, validates the calling agent is a declared peer.
- `Archer.Cli.Commands`: extend `archer workflow events` to render peer chats with speaker labels and revision boundaries.
- Events: `PeerTurnEvent`, `SignOffEvent`, `PhaseEscalatedEvent` (when fallback triggers).
- Tests:
  - Selector tests for each implementation (deterministic for round-robin and mention-aware; mocked LLM for selector mode).
  - Peer chat integration test: 3 peers, sign-off-by-architect rule, converges in 5 rounds.
  - Sign-off lapse: peer A signs off on rev 1; architect rewrites to rev 2; A's sign-off no longer counts; phase requires re-sign.
  - Max-rounds fallback: peer phase that never converges; phase escalates with a structured `PhaseEscalatedEvent`; operator can `archer workflow continue --extend-rounds 5` or `--force-complete`.

**Done-when:**
1. The SDLC example's `design-review` phase runs end-to-end with 4 peers and produces a sealed v4 design.
2. Peer chats survive host restart: kill mid-conversation, restart, the peer-chat grain rehydrates and the *next* speaker is invoked correctly.
3. The TUI can attach to a running workflow's peer chat and render it like an agent tab (read-only).

**Open questions:**
- **Concurrent writes during a peer round.** Two peers both try `write_artifact` in the same round. v1 enforces single-writer-per-turn (the selector picks one speaker; only that speaker can call write_artifact). What if the selector is `llm` and the LLM picks a non-author? Proposal: `write_artifact` is privileged to the deciding peer in v1; reviewers comment via `comment_on_artifact`. Authoring-by-non-deciders unlocks in v2 with explicit conflict resolution.
- **Selector failure handling.** LLM selector returns an unrecognised peer id → fall back to round-robin for that turn. LLM is unreachable → fall back permanently for the rest of the phase + emit a warning event.
- **Long peer chats and context window.** A 16-round chat with verbose peers can exceed context. Use the existing `ContextProfile.RecentMessageWindow` per agent — they already have token-budget logic. Reviewers see the recent window + the artifact; they don't need full transcript history.
- **Idle / stuck peers.** A peer "yields" implicitly by saying nothing actionable. Proposal: each peer's turn must produce *either* substantive content *or* a `sign_off` *or* an explicit `pass` (a tool that says "I have nothing to add this round"). Three consecutive passes from the same peer drops them from the round-robin (they can re-engage with `@<peer-id>`).

**Estimated effort:** large. This is the conceptually hardest slice. The grain itself is moderate; the selector pluggability and the failure-mode handling drive most of the effort. Budget for the integration tests to find unknown unknowns.

### Slice 4 — observability, TUI, and operator UX

**Goal:** running workflows is observable, debuggable, and controllable from both CLI and TUI.

**Deliverables:**

- `Archer.Cli.Commands`:
  - `archer workflow list` — running and recent workflows.
  - `archer workflow status <id>` — current state + phase summary.
  - `archer workflow events <id> --follow` — tail event log.
  - `archer workflow continue <id>` — resume after escalation.
  - `archer workflow interrupt <id>` — supersede the active phase.
  - `archer workflow rerun-phase <id> <phase-id>` — replay from a saved point.
- `Archer.Tui`:
  - New tab type for workflows; subviews per phase showing the active conversation.
  - Artifact diff view: show v(N-1) → vN as a side-by-side diff.
  - Peer-chat tab: speaker badges, sign-off indicators, mention highlights.
  - Cost meter (per workflow + per phase) using OTel metrics.
- OTel:
  - `archer.workflow` span containing `archer.phase` spans containing `archer.peer-turn`/`archer.turn` spans.
  - Metrics: `archer.workflow.cost.tokens` (input/output), `archer.workflow.duration`, `archer.phase.duration`, `archer.workflow.escalations`.
- Tests:
  - CLI smoke tests with a running TestCluster.
  - TUI extraction tests for new pure helpers (artifact diff renderer, cost-meter formatter, peer-chat row formatter).

**Done-when:**
1. The SDLC example walkthrough is reproducible with the actual CLI (the doc shows exact commands and expected output).
2. A running workflow can be paused, inspected, resumed, and re-run mid-phase from the TUI.
3. OTel traces showing the full workflow → phase → peer-turn → model-call hierarchy are visible in the Aspire dashboard.

**Estimated effort:** medium. Most of this is mechanical extension of existing surfaces (CLI, TUI tab system).

### Slice 5 — DAG, conditionals, and human-in-the-loop

**Goal:** unlock the runtime's full expressiveness — branching, joining, conditional phases, and human gates.

**Deliverables:**

- `next-phases` and `depends-on` keys promoted from "documented but unused" to fully implemented.
- `phase-condition` predicates: gate a phase on artifact contents, prior critic verdicts, or workflow inputs.
- `mode: human` — pauses the workflow until an operator approves via CLI/TUI. State: `Paused-AwaitingHuman`.
- Parallel branch execution: two phases with no dependency between them run concurrently. Workflow grain handles fan-out and rejoins.
- Cycle phases: a `phase-group` that loops until a predicate (carries over the v1 critic-mode revision-loop generalisation).
- A built-in test phase template (`mode: solo` + a tool whitelist that includes `run_test_command`, plus a structured failure-summary critic).
- Tests:
  - Two parallel branches converging on a join phase.
  - Conditional phase gated on a rubric score.
  - Human-gate phase that times out after configurable duration.
  - Cycle phase that converges in 3 iterations and one that hits its cap.

**Done-when:**
1. A workflow with at least one fan-out, one join, one conditional, and one human gate runs end-to-end.
2. The SDLC example is extended (in [03-sdlc-example](./03-sdlc-example.md)) with a test-implementation phase that uses OS-as-tester.

**Estimated effort:** medium-to-large. The DAG runtime is the core complexity; conditionals and human gates are leaf features bolted on.

### Cross-cutting work (every slice)

- **Documentation:** every shipped feature gets schema + walkthrough updates in the existing `docs/AGENT_DEFINITIONS.md`-style format, with file-path/line-number references.
- **Sonar quality gate:** new code must keep the project at 0 bugs / 0 vulns / 0 smells / coverage ≥ 80% on new lines (current gate setting).
- **Examples folder:** `workflows/examples/` ships at least one working YAML per shipped slice so operators have a copy-paste starting point.

### Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Peer chats drift / never converge | Medium | Wasted tokens; phase escalation | `max-rounds` always set; selector-aware framing; structured sign-off tool with reasoning required |
| LLM selector picks wrong peer / loops | Medium | Drift, long chats | Three-tier selector ladder; round-robin fallback; cap on consecutive same-speaker turns |
| Tool-schema validation false-rejects valid critic output | Medium | Phase stalls | Re-prompt with the schema as a system note; if still malformed, accept the textual content and synthesise a "needs-revision" verdict |
| Multi-repo path collisions (two repos with `src/Foo.cs`) | Low | Tool calls hit wrong file | All path tools require an explicit `repo` argument when workspace has > 1 repo; default-repo only kicks in when the YAML declares it |
| Cost runaway from N-agent fan-out | Medium | Unexpected bill | Per-workflow token budget in YAML; OTel meter; CLI flag `--max-cost $X`; soft kill when exceeded |
| Orleans grain reactivation losing transient peer-chat state | Low | Mid-conversation rewind | Peer-chat grain persists transcript and revision state on every turn; reactivation rehydrates from store |
| YAML hot-reload changing a running workflow's behaviour mid-flight | Low | Surprising state changes | Definition is snapshotted at workflow start (hash captured); in-flight workflows see the snapshot; new runs see the new YAML |

### Out of scope (forever or until decided otherwise)

- **Distributed-by-default workflow execution.** Single-host Orleans cluster is the supported topology; multi-silo would require streams/persistence design that we don't need.
- **Visual workflow editor.** The YAML + TUI text view is sufficient. If/when a UI shows up, it'll be an external tool that produces YAML.
- **Federated agents** (agents owned by different orgs collaborating on a workflow). Far too many security/trust questions; not addressed here.
- **Graph-of-graphs** (a phase whose body is itself a workflow). LangGraph supports subgraphs; we may add this in a v3 if a real use case appears.

### Definition of "done" for the proposal as a whole

This is **a proposal, not a plan**. "Done" for the proposal is: maintainers read it, accept or reject the major design choices, and we write a short ADR-style summary capturing the resolutions. Then implementation begins on slice 1.

The major design choices to accept/reject:

1. **Workflow grain layer (new) instead of overloading agent grains.** Strong recommendation: accept.
2. **Three named phase modes (solo / critic / peer) rather than a single generic mode.** Recommendation: accept; named modes give better defaults, clearer YAML, and easier-to-test code paths.
3. **DAG runtime designed-for from day 1; v1 ships linear-only.** Strong recommendation: accept; ChatDev's evolution validates this.
4. **Workspace as a first-class concept; existing single-repo flows keep working unchanged.** Recommendation: accept.
5. **Artifact authorship gated by phase.** Recommendation: accept (prevents reviewer-write conflicts in peer mode).
6. **Per-phase agent reset (fresh grain per phase).** Recommendation: accept (token cost + determinism + deliberately erasing prompt-poisoning vectors).
7. **Sign-off, comment_on_artifact, write_artifact as new tools.** Recommendation: accept; they're how agents act on the workflow without baking workflow logic into agent prompts.

Open questions deferred to slice work:

- Selector pluggability beyond the three named ones. (Slice 3.)
- Aggregation strategy for critics with weighted vs unanimous. (Slice 2.)
- Cost/budget guardrails. (Slice 4.)
- Human-in-the-loop UX. (Slice 5.)
