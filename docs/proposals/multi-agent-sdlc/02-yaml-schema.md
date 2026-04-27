## Workflow YAML schema

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [03-sdlc-example](./03-sdlc-example.md)

Workflow definitions live in `workflows/*.yaml`, parallel to the existing `agents/*.yaml` and `mcp/*.yaml`. The host registers two source directories at startup (working dir + build output, mirroring the agent loader) and watches for changes — drop a YAML in, reload the workflow registry, no restart.

This document is the canonical reference. The walkthrough in [03-sdlc-example](./03-sdlc-example.md) shows it in action.

### Top level

| Key            | Type    | Required | Default | Notes |
|----------------|---------|----------|---------|-------|
| `id`           | string  | yes      | —       | Unique within the registry. Lower-case, dash/underscore, like agent ids. |
| `version`      | string  | yes      | —       | Schema version pin. Loader rejects unknowns. Currently `"1"`. |
| `description`  | string  | no       | `""`    | Human-readable. Surfaced in CLI/TUI pickers. |
| `workspace`    | mapping | no       | inherits from CLI flags | See [§ Workspace](#workspace). |
| `inputs`       | list    | no       | `[]`    | Declared inputs the operator must supply at start time. |
| `phases`       | list    | yes      | —       | Ordered phases. Must contain at least one. |
| `defaults`     | mapping | no       | —       | Workflow-wide defaults that individual phases can override. |

### Workspace

Defines the repos and where artifacts land. If omitted, the workflow inherits a single-repo workspace from CLI flags (`archer workflow run --repo .`), so simple workflows don't need this block.

```yaml
workspace:
  default-repo: backend            # repo id used when a tool omits `repo:`
  artifact-root: ./docs/workflows  # directory where artifacts are written
  repos:
    - id: backend
      path: ./services/backend     # relative to the operator's CWD or absolute
      description: REST + grain layer
    - id: frontend
      path: ./apps/web
    - id: infra
      path: ../platform/infra
```

| Key             | Type   | Required | Notes |
|-----------------|--------|----------|-------|
| `default-repo`  | string | no       | Must match an `id` in `repos`. If absent, the first repo is used. |
| `artifact-root` | string | no       | Defaults to `<workflow-state-dir>/artifacts`. Path is relative to the operator's CWD (or absolute). Created if missing. |
| `repos[].id`    | string | yes      | Workspace-local id, e.g. `backend`. Tools reference this. |
| `repos[].path`  | string | yes      | Filesystem path. Validated at start; missing repo = workflow start error. |
| `repos[].description` | string | no | Free-text. Surfaced in agent system prompts so they know what's there. |

### Inputs

Workflow-level inputs are typed values the operator provides at `archer workflow run`:

```yaml
inputs:
  - name: requirement
    type: text
    required: true
    description: One-paragraph summary of what we're building.
  - name: target-quarter
    type: text
    required: false
    default: "next"
  - name: stakeholder-emails
    type: list
    required: false
```

| Field | Notes |
|-------|-------|
| `type` | `text`, `list`, `bool`, `int`, `path` (resolved against workspace), `artifact` (reference to a prior artifact id — for chained workflows). |
| `required` | If true and not supplied at run time, the workflow fails to start. |
| `default` | Used when not supplied. |
| `description` | Surfaced in CLI prompt and stamped into the operator-message of the first phase. |

Inputs become available to phases via `inputs.<name>` references.

### Phases

Each phase is a YAML mapping in the `phases` list. Common keys:

| Key                | Type    | Required | Notes |
|--------------------|---------|----------|-------|
| `id`               | string  | yes      | Unique within the workflow. |
| `description`      | string  | no       | Human-readable. |
| `mode`             | enum    | yes      | `solo`, `critic`, or `peer`. |
| `inputs`           | list    | no       | What this phase needs. References to workflow inputs and prior artifacts. |
| `artifacts`        | list    | no       | What this phase must produce. Names declared here are the only ones writeable. |
| `tools-allowed`    | list    | no       | Subset of the agent's normal tool whitelist that's permitted in this phase. Defaults to the agent's full whitelist. |
| `completion`       | mapping | no       | Convergence rule. Defaults vary by mode (see below). |
| `timeout`          | duration | no      | e.g. `15m`. Workflow-level kill-switch for the phase. |
| `retry-on-failure` | mapping | no       | `{ max-attempts: 3, backoff: exponential }`. Default: 1 attempt. |

Mode-specific keys are described below.

#### Common: inputs

```yaml
inputs:
  - workflow: requirement              # workflow-level input by name
  - artifact: prd                      # prior artifact (must have been produced by an earlier phase)
  - artifact: prd
    revision: latest                   # default; or a specific number, or "all" for full history
```

The phase's primary agent gets these as part of its first user message, formatted as a `# Inputs` block.

#### Common: artifacts

```yaml
artifacts:
  - name: prd
    path: PRD.md                       # relative to workspace.artifact-root
    format: markdown                   # markdown | json | yaml | code
    description: Product Requirements Document
    rubric: prd-rubric                 # optional — referenced by critics
```

If a phase declares `name: foo` here, only this phase (and explicitly-named successors) can write to it. The `write_artifact` tool checks this against the workflow grain's currently-active phase.

#### Common: completion

```yaml
completion:
  rule: artifact-written
  artifact: prd
  fallback:
    rule: max-rounds
    rounds: 3
```

Modes have sensible defaults so most phases don't need this block. See [§ Convergence rules](#convergence-rules).

### Mode: solo

One agent, one artifact (or a small set), no peers.

```yaml
- id: discovery
  mode: solo
  description: PO drafts the PRD
  primary: product-owner             # AgentDefinition id
  inputs:
    - workflow: requirement
  artifacts:
    - name: prd
      path: PRD.md
      format: markdown
      rubric: prd-rubric
  completion:
    rule: artifact-written
    artifact: prd
```

Required: `primary` (agent id). The agent is run with the phase-specific framing prepended to its system prompt. Tool whitelist defaults to the agent's full set; can be narrowed via `tools-allowed`.

Default completion: `artifact-written:<first-declared-artifact-name>`.

#### Mode: critic

Solo + a structured review loop. The primary writes, N critics score, primary revises, repeat until convergence.

```yaml
- id: po-review
  mode: critic
  description: Critics evaluate the PRD on quality dimensions
  primary: product-owner
  target-artifact: prd               # the artifact under review
  critics:
    - id: po-critic-clarity
      rubric: clarity-rubric
      weight: 1.0
    - id: po-critic-completeness
      rubric: completeness-rubric
    - id: po-critic-feasibility
      rubric: feasibility-rubric
    - id: po-critic-risk
      rubric: risk-rubric
  revision:
    max-rounds: 3
    pass-threshold: 0.8              # weighted average score; below = revise
    require-unanimous-pass: false    # if true, ALL critics must verdict=pass
  completion:
    rule: critics-pass-or-rounds-exhausted
```

Required: `primary`, `target-artifact`, `critics`. Each critic is an `AgentDefinition` id; a rubric is referenced by id (rubrics live in `rubrics/*.yaml` — see [§ Rubrics](#rubrics)).

Critic execution per round:
1. Primary writes/revises `target-artifact`.
2. Critics fan out in parallel; each is invoked solo with the artifact + rubric and returns a `CriticReport`.
3. Reports aggregated and presented to primary.
4. Primary either revises (next round) or marks accepted (early exit).

Default completion: `critics-pass-or-rounds-exhausted`. After max rounds, the phase succeeds with whatever revision is current — quality is on the operator to inspect.

#### Mode: peer (swarm)

N agents share one conversation. The defining feature: each agent sees what every other agent has said.

```yaml
- id: design-review
  mode: peer
  description: Cross-discipline peer review of the technical design
  target-artifact: tech-design
  peers:
    - architect                      # carries final sign-off authority
    - qa-lead
    - fitness-architect
    - security-lead
  selector: round-robin              # round-robin | llm | mention-aware
  intro-prompt: |
    You're reviewing the technical design for ${inputs.requirement}.
    The full design is in artifact `tech-design`. Engage with the others'
    points; cite line numbers where relevant; propose concrete edits.
  rounds:
    min: 4                           # at least N exchanges before sign-off counts
    max: 12
  completion:
    rule: sign-off
    by: architect                    # the deciding peer
    fallback:
      rule: max-rounds
      action: escalate-to-operator
```

Required: `peers` (≥ 2), `target-artifact`. Defaults: `selector: round-robin`, `rounds.max: 8`, `completion: sign-off:<first-peer>`.

Each peer can use a `sign_off` tool to indicate "I'm satisfied with revision N." Sign-offs are tracked per revision: if the artifact is rewritten, prior sign-offs lapse. The phase ends when the deciding peer signs off (and `rounds.min` is met) or `max` is hit.

`selector` choices:
- `round-robin` — predictable cycle of peers in declared order
- `llm` — a small selector model reads the last message and picks the next speaker (uses the workflow's default deployment, lightweight prompt)
- `mention-aware` — round-robin by default, but if the last message contains an `@<peer-id>` mention, that peer goes next

##### Sign-off tool

```jsonc
{
  "name": "sign_off",
  "parameters": {
    "approved": "bool (required)",
    "reasoning": "string (required, 1-3 sentences)",
    "blocking-concerns": "array of strings (optional, supplied when approved=false)"
  }
}
```

Available only inside peer phases. The peer chat grain validates: only declared peers can call it; only the deciding peer's call closes the phase; one signature per peer per revision.

### Defaults block

Top-level defaults for all phases. Useful when many phases share a fallback timeout or retry policy.

```yaml
defaults:
  timeout: 30m
  retry-on-failure:
    max-attempts: 2
    backoff: linear
  selector: round-robin              # for peer phases
```

Phase-level keys override defaults.

### Rubrics

Rubrics live in `rubrics/*.yaml` and are referenced by id:

```yaml
id: prd-rubric
description: Quality bar for product requirements documents.
dimensions:
  - id: clarity
    description: A reader unfamiliar with the project understands the goal in one read.
    score-scale: [0, 5]
    pass-at: 4
  - id: testability
    description: Acceptance criteria are concrete and falsifiable.
    score-scale: [0, 5]
    pass-at: 3
  - id: scope
    description: Scope is bounded; non-goals are explicit.
    score-scale: [0, 5]
    pass-at: 3
```

Each critic returns a `CriticReport` with one entry per dimension:

```jsonc
{
  "artifact-name": "prd",
  "scores": { "clarity": 5, "testability": 3, "scope": 4 },
  "verdict": "pass",      // pass | needs-revision | fail
  "comments": "Clear goal and bounded scope. The 'success metrics' section ...",
  "blocking-concerns": [],
  "suggested-edits": [
    { "section": "Acceptance criteria", "change": "Make AC2 measurable: 'p99 < 200ms' instead of 'fast'." }
  ]
}
```

Critic agents must use the `comment_on_artifact` tool to file these reports — same gating as `write_artifact` keeps the schema enforced.

### Selector hooks (advanced)

For projects that want custom logic beyond the named selectors, a `selector: custom` mode points at a registered C# delegate:

```yaml
selector:
  type: custom
  id: my-org-design-review-selector
```

The delegate signature:

```csharp
public delegate ValueTask<string> NextSpeakerSelector(
    PeerChatSnapshot snapshot, IReadOnlyList<string> eligiblePeers, CancellationToken ct);
```

Registered via `services.AddArcherWorkflowSelectors(s => s.Add<MySelector>("my-org-design-review-selector"))`. v1 ships only the three named selectors; this is a forward-compat point.

### Hot reload

The registry mirrors `AgentDefinitionRegistry`:
- Top-level `*.yaml` only, no recursion.
- File watcher with 250 ms debounce; on change, reparse and replace the entire registry snapshot.
- A workflow currently *running* keeps its in-memory definition (rehydrated from state); new runs of the same id pick up the new YAML. Versioning is by definition `version` field plus a hash captured at start.

### Validation

Loader-time errors (workflow refused to register):
- duplicate phase ids
- artifact written by phase A and declared as input to phase B but A precedes B in the array → fine; the reverse → error
- a phase references an unknown agent id (validated against `IAgentDefinitionRegistry`)
- `peers: []` in a peer phase or `< 2` peers
- `completion.rule` referencing an unknown rule
- workspace `default-repo` not in `repos`
- `inputs[].workflow` referencing an undeclared workflow input

Runtime errors (workflow fails the phase):
- a tool tries to write an artifact not declared in the active phase
- a critic returns a malformed `CriticReport`
- a peer signs off as a non-declared peer
- workspace repo path no longer exists

### YAML conventions

- Lower-case kebab-case for keys (`target-artifact`, `revision-loop`).
- Lower-case identifier values (`product-owner`, `qa-lead`); the loader doesn't case-fold.
- Strings that look numeric (`version: "1"`) must be quoted to keep them strings.
- `# comments` are preserved by the loader for diagnostics (file:line on errors).

### Worked example

A trimmed but complete workflow demonstrating all three modes is in [03-sdlc-example](./03-sdlc-example.md).
