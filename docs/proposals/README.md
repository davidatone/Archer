## Proposals

RFC-style design documents written before any code is written, kept in source
so the design conversation is reviewable, version-controlled, and discoverable.
Each proposal lives in its own subdirectory containing the design, the research
that informed it, and the implementation roadmap.

A proposal is *not* a plan to implement immediately. The goal is alignment on
load-bearing design choices before slice 1 starts.

### Index

| Proposal | Status | What's in it |
|----------|--------|--------------|
| [eval-framework/](./eval-framework/README.md) | proposal | A small reproducible test harness for measuring agent quality. Eval suites colocated with agents (`agents/<id>.evals.yaml`). Frozen-fixture tasks × per-agent-config matrix × multi-trial pass-rate. Deterministic guardrails (citation / anti-citation / lexical / tool-call audit) **plus an integral LLM judge — every task ships a rubric**. Robustness variants (interrupt / kill-host / delete-file) turn architectural claims into empirical assertions. **Triggered by an external review** that flagged "infrastructure tests do not prove agent quality". A working Python prototype at `evals/run.py` proves the concept; slice 1 ports it to C#. |
| [multi-agent-sdlc/](./multi-agent-sdlc/README.md) | proposal | Workflow runtime layered on top of agents. Solo / critic / peer-swarm phase modes. Multi-repo workspace. Structured artifact authoring. Designed-for-DAG runtime that ships linear-only in v1. Reference walkthrough: requirement → PRD → architect → 4-peer design review. **Downstream of `eval-framework`** — a workflow runtime without an eval suite is the reviewer's worst case scenario ("beautiful machine for producing mediocre artifacts"). |

### When to write a proposal vs. just doing it

Write a proposal when the change:

- adds a new layer or grain type
- changes a public contract on `IArcherAgentGrain`, `IModelTurnRunner`,
  `IToolRegistry`, `IAgentEventSink`, or any persistence interface
- introduces a new top-level YAML schema (alongside `agents/`, `mcp/`, `rubrics/`, …)
- requires reasoning about Orleans grain reentrancy / scheduling, or about
  durability / crash-recovery
- materially changes how cost (tokens, latency) scales

Just do it (with tests + Sonar gate green) when the change is:

- a bug fix
- an extension within an existing contract (a new tool, a new built-in agent)
- a refactor that doesn't change observable behaviour
- a documentation update

### Format

Each proposal subdirectory contains:

- `README.md` — entry point, exec summary, doc map, design choices to accept/reject
- `01-architecture.md`, `02-yaml-schema.md`, etc. — numbered docs covering specific
  facets in increasing detail
- `04-research.md` (or similar) — what existing systems already solved this and
  what we adopt / avoid
- `05-roadmap.md` — phased implementation slices, risks, open questions

Don't agonise over numbering — ordering matters more than the exact filenames.
The numeric prefix is just a reading order hint.
