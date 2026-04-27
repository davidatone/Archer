## Multi-agent SDLC workflows — proposal

**Status:** proposal · **Date:** 2026-04-27 · **Audience:** Archer maintainers + reviewers

This is a proposal for a multi-agent workflow runtime layered on the existing Archer agent framework. It is **not** a plan to start coding — the request was for documents that capture a design which can be reviewed, debated, and refined before any work starts.

### Read in this order

1. [01-architecture](./01-architecture.md) — the runtime: workflow grain, phase modes (solo / critic / peer), artifacts, workspace, persistence, observability.
2. [02-yaml-schema](./02-yaml-schema.md) — the YAML reference for workflow definitions, including phase modes, rubrics, selectors, and convergence rules.
3. [03-sdlc-example](./03-sdlc-example.md) — a concrete end-to-end run: requirement → PO+critics → PRD → architect → 4-peer design review → sealed technical design.
4. [04-research](./04-research.md) — what we learned from ChatDev (1.0 + 2.0), AutoGen, LangGraph, CrewAI, and Magentic-One; what we're adopting and avoiding.
5. [05-roadmap](./05-roadmap.md) — five implementation slices, each end-to-end testable; risks; design choices to accept/reject before slice 1.

### Executive summary

Archer today runs *one agent at a time*. The agent reasons, calls tools, produces a final answer; the operator interacts via a TUI/CLI. This proposal extends Archer with **a workflow layer that orchestrates multiple agents** through phases that produce SDLC artifacts (PRD, technical design, etc.), with three first-class collaboration modes:

- **solo** — one primary agent drafts an artifact.
- **critic** — a primary agent's artifact is scored against rubrics by N parallel critic agents; the primary revises until convergence.
- **peer** (a.k.a. *swarm*) — N agents share one conversation, see each other's messages, negotiate on a single artifact, and converge via a structured sign-off.

The runtime is shaped as a DAG of phases (linear-only in v1, branching/joining in v2). It runs on Orleans grains for durability, uses the existing agent layer unchanged, ships YAML-defined workflows with hot reload, supports multi-repo workspaces, and emits structured events through the existing `IAgentEventSink` for TUI rendering and OTel tracing.

The reference walkthrough — building webhooks for an order-events stream — exercises every mode in one workflow, including a 14-round peer review between architect, QA lead, fitness architect, and security lead, ending with a sealed v4 technical design.

### Why this shape

The design is informed by a deep read of ChatDev (the user-cited reference), plus AutoGen's `GroupChat` / `SelectorGroupChat`, LangGraph's stateful DAG model, CrewAI's hierarchical/sequential modes, and Microsoft Research's Magentic-One. The key research finding: **ChatDev's own team abandoned the linear two-agent chain in 2.0 for a graph runtime**. We don't need to repeat that discovery; we adopt the lesson up front.

Specifically:

- ✅ **Adopted from ChatDev classic:** blackboard as inter-phase contract, phases as first-class units, two-tier config (structure in code, parameters in YAML), composed phases with break-cycle predicates, per-phase agent reset, OS-as-tester for verifiable artifacts.
- ✅ **Adopted from AutoGen:** N-agent group chat with composable termination predicates and a pluggable next-speaker selector.
- ✅ **Adopted from LangGraph:** stateful DAG with checkpointers; reducers for shared state.
- ✅ **Adopted from CrewAI:** YAML-first agent + workflow definitions.
- ✅ **Adopted from Magentic-One:** explicit progress/task ledger as first-class durable state.
- ❌ **Avoided from ChatDev classic:** two-agent-only restriction, trivial role registry, hardcoded reflection role pair, magic-string-only termination, linear-only chains, two-LLM-calls-per-turn, synchronous global blackboard, per-process artifact storage with no versioning.

[04-research](./04-research.md) has the full comparison table and the rationale for each.

### Design principles

1. **Compose, don't rewrite.** Existing pieces (`IArcherAgentGrain`, `IModelTurnRunner`, `IToolRegistry`, MCP, persistence, OTel) are *unchanged*. The workflow layer drives them; it doesn't duplicate them.
2. **Durability first.** Workflows can pause (mid-phase, awaiting human input, or on host restart) and resume. Orleans grain state + event-sourced artifact history make this natural.
3. **Declarative agents and workflows; imperative runtime.** YAML defines what's wired together; C# defines how the named modes execute. We don't try to express phase mode behaviour in YAML.
4. **N-agent collaboration is a first-class shape, not a bolt-on.** Solo and critic are not just degenerate cases of peer mode — they have meaningfully different default behaviour, defaults, tooling, and observability. Hence three modes, not one.
5. **The workspace is the unit of project context.** Multi-repo is the common case for an architect; single-repo is the special case where `default-repo` is the only repo. The schema reflects that.
6. **Observability is non-negotiable.** Every phase advance, every artifact write, every sign-off, every escalation emits a typed event. The TUI/CLI/OTel pipelines all feed off the same stream.
7. **Cost is visible.** Multi-agent workflows multiply token cost by N; we measure it (OTel metric), surface it (CLI/TUI), and bound it (per-workflow budget guard).

### What's deferred

- Visual workflow editor.
- Distributed-multi-host workflows (single Orleans silo is sufficient).
- Federated agents (cross-org collaboration).
- Workflows-of-workflows (subgraph composition).
- Probabilistic / model-driven phase routing (LLM picks the next phase).

These are noted in [05-roadmap](./05-roadmap.md) under "Out of scope."

### Decisions to accept/reject before any code is written

These are the load-bearing design choices. If a maintainer disagrees with one, the rest changes. Listed in [05-roadmap](./05-roadmap.md) and reproduced here:

1. **A new workflow-grain layer instead of overloading the agent grain.**
2. **Three named phase modes (`solo` / `critic` / `peer`) instead of a single generic mode.**
3. **DAG runtime designed-for from day 1; v1 ships linear-only.**
4. **Workspace as a first-class concept; existing single-repo flows keep working unchanged.**
5. **Artifact authorship gated by phase.**
6. **Per-phase agent reset (fresh grain per phase).**
7. **`sign_off`, `comment_on_artifact`, `write_artifact` as new tools.**

If accepted, slice 1 of [05-roadmap](./05-roadmap.md) becomes the first work item.

### Document map

```
docs/proposals/multi-agent-sdlc/
├── README.md              ← you are here
├── 01-architecture.md     runtime, grains, modes, artifacts, workspace, persistence
├── 02-yaml-schema.md      workflow YAML reference (top-level, phases, modes, rubrics)
├── 03-sdlc-example.md     concrete walkthrough (PRD → review → tech design → 4-peer review)
├── 04-research.md         ChatDev / AutoGen / LangGraph / CrewAI / Magentic-One comparison
└── 05-roadmap.md          five implementation slices + risks + open questions
```

Total ≈ 2,500 lines of design across these five docs. Read time ≈ 45-60 minutes for the full set; 10-15 minutes for this README + the architecture doc to grok the shape.
