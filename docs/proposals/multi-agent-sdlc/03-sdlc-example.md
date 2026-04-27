## SDLC walkthrough — concrete end-to-end

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [02-yaml-schema](./02-yaml-schema.md)

This document walks one full workflow run end-to-end: from the operator typing `archer workflow run sdlc-feature` through to a sealed technical design ready for implementation. It exercises all three phase modes and the multi-repo workspace.

We use a fictional but realistic feature: **"Webhooks for the order-events stream."** The shop has an existing backend (Orleans + EF Core) and a customer-facing web UI; both repos may need changes.

### The workspace

```
~/work/shop/                       # operator's terminal CWD
├── services/backend/              # repo: backend
├── apps/web/                      # repo: frontend
├── platform/infra/                # repo: infra
└── workflows/                     # workflow YAMLs (drop-in directory)
    └── sdlc-feature.yaml
```

When the operator runs `archer workflow run sdlc-feature --requirement "Add webhooks…"`, the host:
1. Loads `workflows/sdlc-feature.yaml` from the registry.
2. Creates a workflow id `workflow_OEPM3X9Z2P40` and a per-workflow state directory at `~/work/shop/.archer/workflows/workflow_OEPM3X9Z2P40/`.
3. Asks the user to confirm the workspace (3 repos detected).
4. Starts the workflow.

### The workflow YAML

```yaml
id: sdlc-feature
version: "1"
description: Take a one-paragraph requirement through PRD → tech-design with peer review.

inputs:
  - name: requirement
    type: text
    required: true
    description: One paragraph: what we're building, for whom, and why now.

workspace:
  default-repo: backend
  artifact-root: ./.archer/workflows/${workflow.id}/artifacts
  repos:
    - id: backend
      path: ./services/backend
      description: Orleans + EF Core. Owns the order-events stream.
    - id: frontend
      path: ./apps/web
      description: Next.js. Customer-facing UI for order tracking.
    - id: infra
      path: ./platform/infra
      description: Terraform. AWS account, queues, secrets.

defaults:
  timeout: 30m

phases:
  - id: discovery
    mode: solo
    description: PO drafts the PRD from the requirement.
    primary: product-owner
    inputs:
      - workflow: requirement
    artifacts:
      - name: prd
        path: PRD.md
        format: markdown
        rubric: prd-rubric

  - id: po-review
    mode: critic
    description: Critics evaluate the PRD on quality dimensions.
    primary: product-owner
    target-artifact: prd
    critics:
      - id: po-critic-clarity
        rubric: clarity-rubric
      - id: po-critic-completeness
        rubric: completeness-rubric
      - id: po-critic-feasibility
        rubric: feasibility-rubric
      - id: po-critic-risk
        rubric: risk-rubric
    revision:
      max-rounds: 3
      pass-threshold: 0.8
      require-unanimous-pass: false

  - id: architecture
    mode: solo
    description: Architect produces the technical design from the PRD.
    primary: architect
    inputs:
      - artifact: prd
    artifacts:
      - name: tech-design
        path: TECH_DESIGN.md
        format: markdown
        rubric: tech-design-rubric
    tools-allowed:
      - read_artifact
      - write_artifact
      - list_files
      - grep
      - search_pattern
      - "*"           # all MCP tools the architect has whitelisted

  - id: design-review
    mode: peer
    description: Cross-discipline peer review of the technical design.
    target-artifact: tech-design
    peers:
      - architect
      - qa-lead
      - fitness-architect
      - security-lead
    selector: mention-aware
    intro-prompt: |
      You're reviewing the technical design for the requirement:

      > ${inputs.requirement}

      The design is in artifact `tech-design`. Engage with the others' points.
      Use @mentions to direct questions. Cite specific sections. Be concrete:
      propose edits in the form "in section X, change ... to ...".

      Sign-off authority rests with @architect after addressing concerns.
    rounds:
      min: 6
      max: 16
    completion:
      rule: sign-off
      by: architect
      fallback:
        rule: max-rounds
        action: escalate-to-operator
```

### The agent definitions

These reference existing `agents/*.yaml` (created in the [roadmap](./05-roadmap.md) phase 1). Schema is the existing `AgentDefinition` — see `docs/AGENT_DEFINITIONS.md`. Sketches:

`agents/product-owner.yaml`
```yaml
id: product-owner
description: Drafts and revises Product Requirements Documents (PRDs).
model: { deployment: gpt-5.3-codex }
instructions: |
  You are a product owner. Your job in this phase is to translate a one-paragraph
  requirement into a complete PRD.

  Structure your PRD with these sections (in order):
  - Goal (one sentence; the change we want to see in the world)
  - Background and motivation (why now)
  - Users and use cases (concrete personas + scenarios)
  - Scope (in and out)
  - Functional requirements (numbered, atomic, testable)
  - Acceptance criteria (numbered, falsifiable; "p99 < 200ms" not "fast")
  - Success metrics (how we'll know it worked)
  - Risks and dependencies
  - Open questions (with named owners)

  Write the PRD using the `write_artifact` tool. Do not prose at the user; emit
  the artifact and stop.

  When given critic reports, revise the artifact to address each blocking concern
  and high-impact suggestion. Use `write_artifact` again with a revised body and
  a one-line change summary. If you reject a suggestion, say so in the summary.
tools:
  - read_artifact
  - write_artifact
  - list_files
  - grep
context:
  recent-message-window: "30%"
  pin-first-message: true
```

`agents/po-critic-clarity.yaml`
```yaml
id: po-critic-clarity
description: Evaluates PRDs against the clarity rubric.
model: { deployment: gpt-5-mini }
instructions: |
  You are a critic specialising in document clarity. Read the artifact and the
  rubric. Score each dimension on its declared scale. Be specific.

  Use `comment_on_artifact` to file your structured report. Do not write to the
  artifact directly.

  A pass means a competent reader unfamiliar with the project understands the
  goal in one read, and the structure is consistent and scannable.
tools:
  - read_artifact
  - comment_on_artifact
context:
  recent-message-window: "60%"
  pin-first-message: true
```

`agents/architect.yaml`
```yaml
id: architect
description: Designs systems. Reads the codebase, writes a technical design.
model:
  deployment: gpt-5.3-codex
  reasoning: { effort: high, summary: detailed }
instructions: |
  You are a software architect. From a PRD and the workspace's repos, produce
  a technical design covering:
  - System overview (one diagram in mermaid; one paragraph)
  - Components touched (per repo: which files/modules, why)
  - Data flow (sequence-style narrative; mermaid sequence diagram if useful)
  - API and contract changes (signatures, breaking-change classification)
  - Persistence model (schema changes, migrations)
  - Operational concerns (config, deploys, rollouts, rollback)
  - Test plan summary (left to QA to flesh out — list categories only)
  - Open questions

  Use list_files / grep / search_pattern to ground your design in the existing
  code. Cite file paths. Don't invent APIs that don't exist.

  When in a peer review, hold the artifact's authorship: incorporate concrete
  proposals you accept; explain rejections briefly. Sign off via the
  `sign_off` tool when you believe the design is ready, or block with
  specific concerns.
tools:
  - read_artifact
  - write_artifact
  - list_files
  - grep
  - search_pattern
  - sign_off
context:
  recent-message-window: "40%"
  pin-first-message: true
```

`agents/qa-lead.yaml`, `agents/fitness-architect.yaml`, `agents/security-lead.yaml` follow the same shape with role-specific guidance and a `sign_off` tool.

### The rubrics

`rubrics/prd-rubric.yaml`
```yaml
id: prd-rubric
description: Quality bar for PRDs.
dimensions:
  - { id: clarity,        score-scale: [0, 5], pass-at: 4 }
  - { id: completeness,   score-scale: [0, 5], pass-at: 4 }
  - { id: testability,    score-scale: [0, 5], pass-at: 3 }
  - { id: scope,          score-scale: [0, 5], pass-at: 3 }
  - { id: feasibility,    score-scale: [0, 5], pass-at: 3 }
  - { id: risk-coverage,  score-scale: [0, 5], pass-at: 3 }
```

(Smaller rubrics specialised per critic role keep critic prompts focused; the per-critic rubric reference in the YAML picks the dimensions relevant to that critic.)

### Run trace

The operator runs:

```
$ archer workflow run sdlc-feature \
    --workspace ~/work/shop \
    --requirement "Add webhooks for the order-events stream so SaaS partners
                   can subscribe to fulfilment milestones without polling.
                   Must support replay, signed payloads, retries with
                   exponential backoff, and per-partner rate limits."

✔ workflow_OEPM3X9Z2P40  started
  workspace: ~/work/shop  (3 repos)
  artifact root: ~/work/shop/.archer/workflows/workflow_OEPM3X9Z2P40/artifacts
  open events: archer events workflow_OEPM3X9Z2P40 --follow
```

#### Phase 1: discovery (solo) — ~45s

The workflow grain spawns a fresh `product-owner` agent grain (`agent_workflow_OEPM3X9Z2P40_discovery_product-owner`). The first user message is composed from the phase's `inputs`:

```
# Phase: discovery
You are running phase `discovery`. Mode: solo.

## Inputs
### requirement
Add webhooks for the order-events stream so SaaS partners can subscribe to
fulfilment milestones without polling. Must support replay, signed payloads,
retries with exponential backoff, and per-partner rate limits.

## Artifacts you must produce
- `prd` → PRD.md (markdown). Rubric: prd-rubric.

## Tools available
read_artifact, write_artifact, list_files, grep
```

The agent thinks briefly, then calls `write_artifact(name="prd", body="…full PRD…", summary="initial draft")`. The tool succeeds, emits `ArtifactWrittenEvent { revision: 1, author: agent_workflow_OEPM3X9Z2P40_discovery_product-owner, summary: "initial draft" }`. The artifact is persisted at `…/artifacts/PRD.md.v1.md` and the `current` pointer updates.

The workflow grain sees `completion: artifact-written:prd` is satisfied, emits `PhaseCompletedEvent`, advances to `po-review`.

Console (events tab):
```
[12:01:14] WORKFLOW_STARTED  workflow_OEPM3X9Z2P40
[12:01:14] PHASE_STARTED     discovery (solo, primary=product-owner)
[12:01:23] MODEL_STARTED     gpt-5.3-codex
[12:01:42] TOOL_CALL_STARTED write_artifact prd
[12:01:42] ARTIFACT_WRITTEN  prd v1 by product-owner — "initial draft"
[12:01:42] PHASE_COMPLETED   discovery (8s, 1 turn, 1 artifact)
```

#### Phase 2: po-review (critic) — ~3 minutes

The workflow grain reads the now-written `prd` and fans out to four critics in parallel. Each critic is a fresh agent grain seeded with:

```
# Phase: po-review (critic, round 1 of 3)
You are critiquing artifact `prd` against rubric `clarity-rubric`.

## Artifact (current revision: 1)
<full PRD body>

## Rubric
- clarity (0-5, pass at 4): A reader unfamiliar with the project understands…

## Required output
Use `comment_on_artifact` to file your CriticReport.
```

Each critic completes a single turn (one model call + one tool call) and returns a `CriticReport`. They run concurrently — total wall time ≈ slowest critic, not sum.

```
[12:01:42] PHASE_STARTED     po-review (critic, primary=product-owner, critics=4)
[12:01:42] MODEL_STARTED     gpt-5-mini  agent=po-critic-clarity
[12:01:42] MODEL_STARTED     gpt-5-mini  agent=po-critic-completeness
[12:01:42] MODEL_STARTED     gpt-5-mini  agent=po-critic-feasibility
[12:01:42] MODEL_STARTED     gpt-5-mini  agent=po-critic-risk
[12:02:11] CRITIC_REPORT     po-critic-clarity → prd v1: pass (4.5)
[12:02:14] CRITIC_REPORT     po-critic-completeness → prd v1: needs-revision (3.0)
[12:02:18] CRITIC_REPORT     po-critic-feasibility → prd v1: pass (4.0)
[12:02:22] CRITIC_REPORT     po-critic-risk → prd v1: needs-revision (2.5)
```

Aggregated weighted score is below the 0.8 pass threshold; two critics request revisions. The workflow grain hands the reports back to the primary product-owner (a NEW agent grain — fresh history — see `agent_workflow_OEPM3X9Z2P40_po-review_product-owner_r2`):

```
# Phase: po-review (revision round 2 of 3)
The previous PRD revision (v1) received the following critic feedback:

### po-critic-completeness — needs-revision (score: 3.0/5.0)
Missing acceptance criteria for retry behaviour. Section 6 says "retries with
exponential backoff" but doesn't define max attempts, base delay, or jitter.
Suggested edit: add AC7-AC9 covering retry parameters and a dead-letter rule.

### po-critic-risk — needs-revision (score: 2.5/5.0)
Replay risk is mentioned but not bounded. What's the replay window? Are signed
payloads valid forever, or do signatures expire? Per-partner rate limits could
become DoS amplification — discuss the upper bound.

(Other critics passed.)

Revise the PRD via write_artifact. Address every blocking concern; brief
summary of changes in the tool call's `summary` parameter.
```

The PO writes v2:

```
[12:02:24] MODEL_STARTED     gpt-5.3-codex  agent=product-owner (revision 2)
[12:02:51] TOOL_CALL_STARTED write_artifact prd
[12:02:51] ARTIFACT_WRITTEN  prd v2 by product-owner — "added retry + replay AC; bounded rate limits"
```

Round 2 fans out to critics again (each gets the new revision):

```
[12:02:52] CRITIC_REPORT     po-critic-clarity → prd v2: pass (5.0)
[12:02:55] CRITIC_REPORT     po-critic-completeness → prd v2: pass (4.5)
[12:02:58] CRITIC_REPORT     po-critic-feasibility → prd v2: pass (4.0)
[12:03:03] CRITIC_REPORT     po-critic-risk → prd v2: pass (4.0)
[12:03:03] PHASE_COMPLETED   po-review (1m21s, 2 rounds, 2 revisions)
```

All critics pass; phase completes.

#### Phase 3: architecture (solo) — ~6 minutes

A fresh `architect` agent grain reads the PRD and explores the workspace. Architect's tool whitelist includes `list_files`, `grep`, `search_pattern` against any of the 3 repos. The agent does ~10 reads and ~3 searches, then writes `tech-design`:

```
[12:03:03] PHASE_STARTED     architecture (solo, primary=architect)
[12:03:14] TOOL_CALL_STARTED list_files repo=backend path=src/Domain/Orders
[12:03:14] TOOL_CALL_STARTED list_files repo=backend path=src/Application/Events
[12:03:18] TOOL_CALL_STARTED grep repo=backend pattern="OrderStatusChanged"
[12:03:23] TOOL_CALL_STARTED search_pattern repo=infra pattern="webhook|outbox"
[12:03:31] REASONING         "The events stream is already exposed via an outbox table…"
[12:03:48] TOOL_CALL_STARTED search_pattern repo=backend pattern="IOrleansStream|StreamProvider"
[12:04:02] TOOL_CALL_STARTED list_files repo=backend path=src/Persistence
[12:05:55] TOOL_CALL_STARTED write_artifact tech-design
[12:05:55] ARTIFACT_WRITTEN  tech-design v1 by architect — "initial design: outbox-based webhooks…"
[12:05:55] PHASE_COMPLETED   architecture (2m52s, 1 turn, 8 tool calls, 1 artifact)
```

Note the wall-clock time is dominated by reasoning + tool calls (the model has `reasoning: { effort: high }`).

#### Phase 4: design-review (peer/swarm) — ~12 minutes

This is the headline phase. Four agents — architect, qa-lead, fitness-architect, security-lead — share one conversation, each one bringing their perspective to the design.

The peer-chat grain (`peerchat_workflow_OEPM3X9Z2P40_design-review`) is the orchestrator. It owns the shared transcript. Each round it picks a speaker per the `mention-aware` selector (round-robin by default; if the last message contains `@<peer-id>`, that peer goes next).

Round 1 — the architect speaks first because they authored the artifact (selector convention: artifact author opens):

```
[12:05:55] PHASE_STARTED     design-review (peer, peers=4, selector=mention-aware)
[12:05:56] PEER_TURN         round=1 speaker=architect
[architect]: "Posted v1 of the tech design. Open to feedback. Key choices:
  outbox table in backend with a polling worker, HMAC-SHA256 signing keyed per
  partner, async retries via Orleans reminders with exponential backoff
  (capped 24h). Per-partner concurrency limit = 10. @qa-lead, what test
  coverage do you want? @security-lead, the HMAC scheme + key rotation —
  flag concerns now."
```

The selector parses the @mentions: `qa-lead` and `security-lead` are queued. Round-robin yields `qa-lead`:

```
[12:06:48] PEER_TURN         round=2 speaker=qa-lead
[qa-lead]: "Test plan view: at minimum I want
  (a) contract tests for the public webhook DTOs (SHOULD-NOT-CHANGE),
  (b) integration tests with a fake partner endpoint validating signature +
      retry math (3 happy paths, 4 unhappy: 4xx-permanent, 4xx-rate-limit,
      5xx-transient, 5xx-fatal),
  (c) chaos tests around outbox-worker restart mid-batch (must be exactly-once
      delivery from partner perspective).
  Concrete coverage target: 90% line + 80% branch on the outbox + signing
  modules. @architect, the design doesn't mention an idempotency key — how
  does the partner deduplicate retries? Without that we can't claim
  exactly-once."
```

Selector: explicit @architect mention. Architect responds:

```
[12:08:02] PEER_TURN         round=3 speaker=architect
[architect]: "Good catch on idempotency. Adding: each event carries a
  partner-scoped event_id; we keep last 24h of delivered ids per-partner in
  Redis (with persistence) for partner-side dedup grace. Updating the design
  now. @security-lead, your turn — HMAC + key rotation."
[12:08:04] TOOL_CALL_STARTED write_artifact tech-design
[12:08:04] ARTIFACT_WRITTEN  tech-design v2 by architect — "added partner-scoped event_id + dedup grace"
```

The architect rewrites the artifact mid-conversation. The peer-chat grain emits both the message and the `ArtifactWrittenEvent`; everyone's existing sign-offs (none yet at this revision) lapse, since the artifact moved.

Selector hands to security-lead:

```
[12:08:05] PEER_TURN         round=4 speaker=security-lead
[security-lead]: "HMAC-SHA256 with per-partner secret is fine. Three concerns:
  (1) v2 mentions Redis for dedup — secrets stored where? Reuse AWS Secrets
      Manager (already provisioned per @infra), don't roll our own KMS.
  (2) Signature payload must include a timestamp + nonce, otherwise a partner
      who logs the body can replay it forever. Add Date header (ISO 8601) and
      an X-Webhook-Nonce; reject deliveries with timestamp > 5min skew.
  (3) Replay window: design says 'unbounded'. Set 7 days max; older replays
      require operator approval. This caps the blast radius if a partner key
      leaks.
  @fitness-architect, want to weigh in on the Redis-for-dedup choice — that's
  a new dependency."
```

```
[12:09:10] PEER_TURN         round=5 speaker=fitness-architect
[fitness-architect]: "Redis is fine — already in stack for session cache.
  But scaling concern with the design as-stated: outbox polling worker is
  single-tenant. With 200+ partners @ p99 throughput target = 5k events/min
  per partner = 1M events/min aggregate, the polling worker becomes a
  serialisation bottleneck. Need partitioning by partner_id (hash to N
  workers) with Orleans grain affinity. Also: per-partner rate limit + global
  rate limit + per-partner queue depth alarm; without those we have a noisy-
  neighbour problem. @architect, please add a capacity section showing
  expected throughput, partition strategy, and back-pressure on overflow."
```

```
[12:10:31] PEER_TURN         round=6 speaker=architect
[architect]: "Acknowledging all three. Updating design:
  - Section 5 (Persistence): Redis for dedup grace; secrets in AWS Secrets
    Manager (per @security-lead)
  - Section 4 (API contracts): Date header + X-Webhook-Nonce + 5min skew
  - Section 9 (Operational): 7-day replay window cap; operator override path
  - New section 10 (Capacity & scaling): partitioned outbox workers (16
    partitions, hash on partner_id), per-partner queue depth alarm at 1k,
    global rate limit at 50k events/min, back-pressure: drop-tail with
    operator alert at sustained throughput > 80% capacity.
  Posting v3 now."
[12:10:33] TOOL_CALL_STARTED write_artifact tech-design
[12:10:33] ARTIFACT_WRITTEN  tech-design v3 by architect — "addressed sec/fitness concerns; added capacity section"
```

Rounds 7-9 cycle through the peers reviewing v3. Each finds smaller issues (qa-lead wants concrete numbers in the test plan; fitness wants a load-test commitment; security signs off pending implementation review). One more revision lands as v4.

```
[12:14:18] PEER_TURN         round=10 speaker=architect
[architect]: "v4 addresses the remaining points: load-test SLO doc-linked,
  per-test concrete numbers in QA section. I believe we're ready. Calling
  for sign-offs."
```

Each peer is asked in turn (round-robin from architect):

```
[12:14:19] PEER_TURN         round=11 speaker=qa-lead
[qa-lead]: "Approved on v4 — coverage targets and test categories are clear."
[12:14:19] TOOL_CALL_STARTED sign_off approved=true
[12:14:19] SIGNED_OFF        qa-lead → tech-design v4

[12:14:23] PEER_TURN         round=12 speaker=fitness-architect
[fitness-architect]: "Approved — capacity section addresses the partition + alarm concerns."
[12:14:23] SIGNED_OFF        fitness-architect → tech-design v4

[12:14:27] PEER_TURN         round=13 speaker=security-lead
[security-lead]: "Approved on v4 — pending one runtime check: the HMAC code
  must be reviewed at PR time, not just spec-time. Filing as a design-review
  gate, not blocking signoff."
[12:14:27] SIGNED_OFF        security-lead → tech-design v4 (with note)

[12:14:31] PEER_TURN         round=14 speaker=architect
[architect]: "All three peers approved. Sealing v4."
[12:14:31] TOOL_CALL_STARTED sign_off approved=true
[12:14:31] SIGNED_OFF        architect → tech-design v4 (deciding peer)
[12:14:31] PHASE_COMPLETED   design-review (8m36s, 14 rounds, 4 artifact revisions)
[12:14:31] WORKFLOW_COMPLETED workflow_OEPM3X9Z2P40
```

#### Final state

On disk after the workflow:

```
~/work/shop/.archer/workflows/workflow_OEPM3X9Z2P40/
├── state.json                       # current snapshot
├── events.ndjson                    # full event log (≈300 events)
└── artifacts/
    ├── PRD.md                       # symlink → PRD.md.v2.md
    ├── PRD.md.v1.md
    ├── PRD.md.v2.md
    ├── TECH_DESIGN.md               # symlink → TECH_DESIGN.md.v4.md
    ├── TECH_DESIGN.md.v1.md
    ├── TECH_DESIGN.md.v2.md
    ├── TECH_DESIGN.md.v3.md
    └── TECH_DESIGN.md.v4.md
```

The operator gets a final summary:

```
$ archer workflow status workflow_OEPM3X9Z2P40

workflow_OEPM3X9Z2P40  COMPLETED
  Definition: sdlc-feature v1
  Workspace:  ~/work/shop  (3 repos)
  Started:    2026-04-27 12:01:14
  Finished:   2026-04-27 12:15:36
  Duration:   14m 22s
  Cost:       ≈ 18,400 input tokens, 6,200 output, $0.21

Phases:
  ✓ discovery       solo    8s     1 turn,  1 artifact
  ✓ po-review       critic  1m21s  2 rounds, 2 revisions, 8 critic reports
  ✓ architecture    solo    2m52s  1 turn, 8 tool calls, 1 artifact
  ✓ design-review   peer    8m36s  14 rounds, 4 artifact revisions, 4 sign-offs

Artifacts:
  prd          v2 (final)   ./.archer/workflows/.../artifacts/PRD.md
  tech-design  v4 (final)   ./.archer/workflows/.../artifacts/TECH_DESIGN.md

Next:  Hand to implementation. (`archer workflow run sdlc-implement --tech-design <id>` — out of scope for v1.)
```

### What this run demonstrates

| Feature | Where exercised |
|---------|-----------------|
| Solo phase | discovery, architecture |
| Critic phase | po-review (4 critics, 2 rounds, structured rubric reports) |
| Peer/swarm phase | design-review (4 peers, 14 rounds, mention-aware selector, 4 revisions) |
| Multi-repo workspace | architect's `list_files repo=backend`, `grep repo=infra`, etc. |
| Artifact versioning | PRD.md v1→v2; TECH_DESIGN.md v1→v2→v3→v4 |
| Sign-off mechanic | Each peer's structured `sign_off` call; deciding peer (architect) closes |
| Sign-off lapse | When architect rewrote v1→v2 mid-conversation, no prior sign-offs existed; subsequent revisions lapsed any earlier sign-offs |
| Mention-aware selector | `@qa-lead`, `@architect`, `@fitness-architect` redirected the conversation |
| Convergence | `sign-off:architect` closed the phase; the `max-rounds: 16` fallback wasn't needed |
| Per-phase agent reset | The PO in po-review was a fresh agent grain (didn't carry discovery context); same for revision rounds |
| OTel tracing | One `archer.workflow` span containing four `archer.phase` spans; the peer phase span containing 14 `archer.peer-turn` spans |

### Failure modes demonstrated by *not* happening here

For the roadmap and v2 to consider:
- A critic's rubric report failing to parse (LLM didn't follow the structured tool schema). Fallback: re-prompt; if still bad, treat as "needs-revision" with a generic comment.
- The deciding peer never signs off and `max-rounds: 16` is hit. Fallback per YAML: `escalate-to-operator` — workflow pauses, operator gets a CLI prompt to approve / reject / extend rounds.
- Two peers want contradictory things (qa wants more types, fitness wants less). The selector keeps cycling; the deciding peer (architect) ultimately arbitrates by writing the resolution and signing off.
- A peer abuses `write_artifact` to overwrite the deciding peer's authorship. Mitigation in v1: the workflow grain only allows the *deciding peer* to author; reviewers comment via `comment_on_artifact` (which doesn't change the body, just files structured feedback into the chat).

These are dealt with in [05-roadmap](./05-roadmap.md).
