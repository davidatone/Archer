## Eval framework — architecture

**Status:** proposal · **See also:** [README](./README.md), [02-task-schema](./02-task-schema.md), [03-grading](./03-grading.md), [04-roadmap](./04-roadmap.md)

> **Plain-English summary.** This doc explains how the runner is built. Three pieces: a YAML file with the question, a runner that spawns the agent and captures its answer, and a grader that decides pass/fail. The runner reuses the existing CLI code path so the agent runs *exactly* the way it would in real life. Each question runs 5 times because LLMs are random, and the same question can target multiple agent variants in one go (smart vs. cheap vs. no-reasoning) so you get a side-by-side table. A "fixture" is a frozen snapshot of the codebase the agent searches against — it has to be frozen so the question's expected answer doesn't drift as the repo changes.

### Goals

1. **Prove agent quality, not framework correctness.** Existing tests cover persistence, fencing, the model runner, the TUI shell. They say nothing about whether `code-scout` actually answers questions correctly. Evals fix that.
2. **Compare configurations objectively.** The same task run against `code-scout` (gpt-5-codex + reasoning=high), `code-scout-cheap` (gpt-5-mini + reasoning=low), and a baseline "no reasoning" variant should produce a result table you can read in 30 seconds.
3. **Catch regressions deterministically.** A PR that breaks the agent's ability to cite `src/Archer.Actors/Grains/ArcherAgentGrain.cs` for the prompt "where is the agent grain implemented?" should fail in CI, not in production.
4. **Stay cheap.** Running the suite must cost ≤ a few dollars and finish in ≤ 10 minutes for the bootstrap suite, otherwise nobody runs it.
5. **Stay reproducible.** A snapshot of the target repo, a frozen prompt, a captured cost+output ledger — anyone running the same eval at the same git SHA should see comparable numbers.

### Non-goals

- A general-purpose LLM evaluation framework. We adopt a vocabulary that's intentionally Archer-specific.
- Replacing manual UX inspection. Evals catch correctness regressions; they don't tell you the chat UI is awkward.
- Public leaderboard / external benchmark. The eval is for *us*; if it ever ships externally, that's a separate exercise.
- Live-repo evaluation. Real repos drift; eval tasks must run against frozen fixtures.

### What an eval is, in three pieces

```
┌──────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│   Task spec      │     │     Runner       │     │     Grader       │
│   (YAML)         │ ──> │  spawns agent    │ ──> │  pass / fail     │
│                  │     │  captures        │     │  + cost + time   │
│  prompt          │     │   final answer,  │     │                  │
│  expected        │     │   tool calls,    │     │                  │
│  agent-matrix    │     │   events,        │     │                  │
│  budget          │     │   tokens, time   │     │                  │
│  trials          │     │                  │     │                  │
└──────────────────┘     └──────────────────┘     └──────────────────┘
```

Nothing is invented from scratch:
- The **runner** is a thin wrapper over `IArcherAgentGrain` — same code path the CLI uses.
- The **agent** is a normal `AgentDefinition` resolved by id from the existing registry.
- **Tool calls and events** are read out of the existing `IAgentEventSink` NDJSON log.
- **Tokens and durations** are read out of the existing OTel meter (`archer.model.call.duration`, `archer.model.tokens`).

The eval-specific code is small: task loader, robustness orchestrator, grader ladder, results writer.

### The agent-config matrix

This is the load-bearing idea. An eval *cell* is a `(task, agent-id, variant)` triple. A task can target multiple agent ids in a single run; variants apply per-agent overrides on top.

```
                              agent definition
                  ┌────────────────────┬───────────────────┬────────────────────┐
                  │  code-scout        │  code-scout-cheap │ code-scout-no-reas │
                  │ (gpt-5-codex,      │ (gpt-5-mini,      │ (gpt-5-mini,       │
                  │  effort=high)      │  effort=low)      │  effort=none)      │
   ┌──────────┬───┴────────────────────┴───────────────────┴────────────────────┤
   │ task 001 │ 5/5 pass · 18k tok · │ 5/5 pass · 4.2k tok ·│ 3/5 pass · 2.1k tok│
   │          │ 47s avg              │ 12s avg              │ 6s avg             │
   ├──────────┼──────────────────────┼──────────────────────┼────────────────────┤
   │ task 002 │ 5/5 pass             │ 4/5 pass             │ 1/5 pass           │
   │ …        │                      │                      │                    │
   └──────────┴──────────────────────┴──────────────────────┴────────────────────┘
```

Cell content: `<trials passed>/<trials run>`, mean tokens, mean wall time. The cell's verdict is `passed` if `passed/run >= required-pass-rate` (default 0.8).

This is the answer to "do I need reasoning for this task?" and "can I get away with the cheap model?" — both questions the agent's YAML can't answer on its own.

#### Variants without YAML proliferation

Three options for declaring agent-config variants:

1. **Distinct YAMLs.** `agents/code-scout.yaml`, `agents/code-scout-cheap.yaml`, `agents/code-scout-no-reasoning.yaml`. Verbose but transparent.
2. **Inheritance.** A new YAML key `inherits: code-scout` that copy-modifies. New surface to maintain.
3. **Eval-side overrides.** The task spec lists `agent-matrix` entries with optional `override` blocks; the runner builds a transient `AgentDefinition` for the cell.

Recommendation: **start with option 1** (distinct YAMLs). Loader is unchanged; eval matrix references existing ids. If we end up with 8 variants of the same agent, revisit. Option 3 (override blocks) is the natural successor — easy to add later, hard to take back.

```yaml
# agents/code-scout.evals.yaml — inside one task entry
agent-matrix:
  - id: code-scout                    # production default — full YAML in agents/
  - id: code-scout-cheap              # cheap variant — full YAML in agents/
  - id: code-scout-no-reasoning       # ablation — full YAML in agents/
```

### Components and where they live

```
src/
├── Archer.Evals/                     ← new project
│   ├── Archer.Evals.csproj
│   ├── EvalRunner.cs                 ← orchestrates a run: load → execute trials → grade → record
│   ├── Tasks/
│   │   ├── EvalSuiteLoader.cs        ← globs agents/*.evals.yaml, parses suites, expands defaults
│   │   ├── TaskSpec.cs               ← Domain model (one per task entry inside a suite)
│   │   └── FixtureLoader.cs          ← reads evals/fixtures/repos/*.fixture.yaml
│   ├── Execution/
│   │   ├── TrialExecutor.cs          ← spawns one agent run, captures artefacts
│   │   ├── RunArtifacts.cs           ← final answer, events, tokens, durations, tool calls
│   │   └── BudgetEnforcer.cs         ← kills the trial when caps are exceeded
│   ├── Grading/
│   │   ├── IGrader.cs
│   │   ├── BudgetGrader.cs
│   │   ├── CitationGrader.cs
│   │   ├── AntiCitationGrader.cs
│   │   ├── LexicalGrader.cs
│   │   ├── ToolCallAuditGrader.cs
│   │   └── LlmJudgeGrader.cs         ← required for every task
│   ├── Robustness/
│   │   └── RobustnessOrchestrator.cs ← interrupt-mid-trial, kill-host, missing-file scenarios
│   └── Reports/
│       ├── JsonResultWriter.cs       ← evals/results/<run-id>.json
│       └── TableRenderer.cs          ← console table + markdown for PR comments
└── Archer.Cli/Commands/
    └── EvalCommand.cs                ← `archer eval run | list | show | diff`

agents/                               ← agent definitions (existing)
├── code-scout.yaml
├── code-scout.evals.yaml             ← NEW: eval suite colocated with the agent it tests
├── code-scout-cheap.yaml
├── critic.yaml
├── critic.evals.yaml
└── …

evals/
├── README.md
├── fixtures/
│   └── repos/
│       ├── archer-2026-04-27.fixture.yaml  ← fixture metadata (path + sha256)
│       └── archer-2026-04-27.tar.gz        ← committed (or git-LFS for big ones)
└── results/                          ← gitignored

tests/
└── Archer.Evals.Tests/               ← unit tests for graders, fixture-loader, budget enforcer
```

### Trial execution

A single trial:

1. **Mount the fixture.** Extract the task's referenced tarball to a temp directory under `~/.archer/eval-runs/<run-id>/<trial>/`. Verify sha256 matches the task spec.
2. **Resolve the agent.** Look up the `AgentDefinition` by id in the existing `IAgentDefinitionRegistry` — fail-fast if it doesn't exist.
3. **Spawn the host.** Same `ArcherHostBuilder.ConfigureArcher(...)` used by the CLI, with the temp dir as the repo root and a per-trial state directory.
4. **Send the prompt.** Through `ICliHost.RunAsync` style — `agent.InitializeAsync(NewAgentRequest{ FirstUserPrompt = task.Prompt, ... })`.
5. **Run to settlement.** Subscribe to `IAgentEventSink`. The trial completes on `TurnCompletedEvent`, `TurnFailedEvent`, or budget exhaustion; whichever comes first wins.
6. **Capture artefacts.**

```csharp
public sealed record RunArtifacts(
    string FinalAnswer,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    int InputTokens,
    int OutputTokens,
    TimeSpan WallTime,
    int TurnCount,
    BudgetExhaustionReason? Exhaustion);
```

7. **Tear down.** Stop the host. Don't delete the per-trial state dir until the grader has read it.

Steps 3–7 already work today — that's how the CLI runs a single agent. The eval's contribution is steps 1, 2, 6, 7 plus the trial loop and grader pipeline.

#### Budget enforcement

Each trial has hard caps:

```yaml
budget:
  per-trial:
    max-output-tokens: 50000
    max-wall-seconds: 120
    max-tool-calls: 30
    max-turns: 10
```

The enforcer subscribes to events:
- `ToolCallCompletedEvent` → tool-call counter
- `TurnStartedEvent` → turn counter
- `ModelStartedEvent` → start a stopwatch per model call (rolls into wall-time)
- A hosted `Timer` for the wall-time cap

When a cap is hit the enforcer calls `agent.InterruptAsync(new InterruptRequest("eval budget exceeded"))` and records the cap as the exhaustion reason. The trial is then graded — a budget exhaustion may still pass *some* graders (e.g. citation if the agent already named the file) but will fail the budget grader.

#### Determinism and stochasticity

Three things make trials non-deterministic:
- LLM sampling (we don't currently set `temperature` from the agent definition; default = model default)
- `DateTime.UtcNow` (recorded but not part of grading)
- Tool ordering (the model picks; we don't constrain)

Mitigations:
- **Multi-trial.** Every cell runs N trials (default 5). Verdict is the pass-rate, not a single result.
- **Pin temperature.** Add an optional `model.temperature` field to the agent definition; default 0 for eval-targeted agents.
- **Pin top_p.** Same.
- **Capture seeds.** If the model API supports a `seed` parameter (Azure OpenAI Responses API does for some models), record it. Different runs with the same seed should converge.

The eval system **is not honest** if it pretends a single trial is the truth. The output table always shows `<passed>/<trials>`, never just "PASS".

### Robustness matrix

The reviewer specifically called out "restart/interruption cases". A task can declare additional variants:

```yaml
robustness:
  - id: baseline                          # always present implicitly
  - id: interrupt-mid-turn
    type: interrupt-at-iteration
    iteration: 2
  - id: kill-host-mid-trial
    type: kill-host-after-tool-calls
    after-tool-calls: 3
    expect: rehydrates-state-after-restart
  - id: missing-file
    type: delete-file-during-fixture-mount
    file: src/Archer.Domain/Agents/AgentMessage.cs
    expect: graceful-failure-no-hallucination
```

Each variant runs as its own row in the result matrix. Graders for robustness variants are different from baseline graders:

- `interrupt-mid-turn` → *the worker's commit must be rejected by the fence*. Pass criterion: events show a `TurnSupersededEvent` for the interrupted turn AND the new prompt's turn produces a fresh `FinalAnswerEvent`.
- `kill-host-mid-trial` → restart the host, agent state must rehydrate, follow-up prompt must succeed.
- `missing-file` → final answer must NOT cite the deleted path; agent must report inability or work around.

These tests **prove the architecture** — fenced turns, durable state, no-hallucination guards. They're the answer to "does the Orleans story actually pay off?"

### Result schema

One JSON file per run, one row per cell × trial:

```json
{
  "run-id": "eval_2026-04-27T09-31-12Z_a1b2c3",
  "git-sha": "ff6baa4",
  "started-at": "2026-04-27T09:31:12Z",
  "finished-at": "2026-04-27T09:38:54Z",
  "fixture-shas": { "archer-2026-04-25": "a1b2c3..." },
  "cells": [
    {
      "task-id": "001-find-grain-impl",
      "agent-id": "code-scout",
      "robustness": "baseline",
      "trials": [
        {
          "trial": 1,
          "verdict": "pass",
          "graders": {
            "budget": {"verdict": "pass", "tokens": 17943, "wall-seconds": 46.2},
            "citation": {"verdict": "pass", "cited": ["src/Archer.Actors/Grains/ArcherAgentGrain.cs"]},
            "anti-citation": {"verdict": "pass"},
            "lexical": {"verdict": "pass", "matched": ["InitializeAsync", "AgentState"]},
            "tool-call-audit": {"verdict": "pass", "expected-files-opened": true},
            "judge": {
              "verdict": "pass", "score": 0.95, "model": "gpt-5-codex",
              "reasoning": "Answer correctly identifies ArcherAgentGrain.cs and explains the InitializeAsync/AddUserMessageAsync/CommitFinalAnswerIfStillActiveAsync flow.",
              "input-tokens": 1842, "output-tokens": 312, "cost-usd": 0.012
            }
          }
        }
      ],
      "summary": {
        "passed": 5, "trials": 5, "pass-rate": 1.0,
        "mean-tokens": 18204, "mean-wall-seconds": 47.1, "estimated-cost-usd": 0.054
      }
    }
  ],
  "totals": {
    "trials": 60, "passed": 53, "pass-rate": 0.883,
    "input-tokens": 412332, "output-tokens": 88421, "estimated-cost-usd": 1.21,
    "wall-seconds": 312
  }
}
```

The console renderer turns this into a scannable table. The CI integration writes it to a PR comment.

### CI integration

```yaml
# .github/workflows/eval.yml (sketch)
on: pull_request

jobs:
  eval:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet build Archer.slnx
      - env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
        run: |
          dotnet run --project src/Archer.Cli -- eval run \
            --output-json eval-results.json \
            --max-cost 5.00 \
            --regression-baseline origin/main
      - name: Comment on PR
        run: dotnet run --project src/Archer.Cli -- eval pr-comment \
            --results eval-results.json --against origin/main
```

The `--regression-baseline` switch fetches the eval result file from the merge base and:
- **Hard-fail** if any task that previously passed (≥ 0.8 pass-rate) now passes < 0.6
- **Soft-warn** if pass-rate dropped 0.1+ but stayed ≥ 0.6
- **No-op** for new tasks added in the PR

This avoids the "evals are flaky, ignore them" failure mode by giving regressions a clear gate while tolerating the inherent stochasticity.

### Cost guardrails

Three layers, all opt-in to make local runs unrestricted:

1. **Per-trial budget** (in the task spec, mandatory).
2. **Per-run cap** (`--max-cost $X` on the CLI). Stops new trials when accumulated estimated cost crosses the line.
3. **Per-cell cap** (`--max-cost-per-cell $X`). Aborts the cell after a few expensive trials so a runaway model doesn't burn budget on one task.

Estimated cost is computed from token counts × current model pricing (a static table in `Archer.Evals` — keep it in source so it's reviewable, update on the rare price changes).

### Observability

The runner is itself OTel-instrumented:

- `archer.eval.run` span (whole suite)
- `archer.eval.task` span (one task across all variants)
- `archer.eval.cell` span (task × agent × robustness)
- `archer.eval.trial` span (one trial)
  - inside which the existing `archer.turn` / `archer.tool.<name>` spans nest

Plus metrics: `archer.eval.cell.pass-rate`, `archer.eval.trial.cost-usd`, `archer.eval.regression.count`. Same OTel pipeline as the rest of Archer; viewable in Aspire.

### What this isn't

- **Not a guarantee of "the agent is good".** A green eval run means "the agent passed the tasks we wrote". The tasks themselves can be too easy, too narrow, or biased toward the agent's known strengths. Eval quality is a function of task quality.
- **Not a replacement for production observability.** A real user asking a question outside the eval distribution gets unmeasurable behaviour. Evals are necessary, not sufficient.
- **Not a substitute for cost vigilance.** A passing eval that cost $4.20 might be fine for CI but unaffordable in production. The cost dimension is reported alongside pass-rate; reviewers must look at both.

### Open questions deferred to roadmap

1. **Live-tool gating.** Some prompts will need the MCP memory server or another stateful tool. Does the eval mock these, or run real ones with a per-task fixture state?
2. **Test agent definitions vs. production ones.** Should `agents/code-scout.yaml` be the eval target, or do we ship `agents/eval/code-scout.yaml` to keep the production agent untouched? Recommendation: same definition, different overrides via per-trial `temperature: 0`.
3. **LLM-as-judge model selection.** A judge needs to be at least as capable as the agent under test, or it'll over-grade. We probably can't use the same family for both. Specifics in [03-grading](./03-grading.md).
4. **Long-tail tasks.** Beyond bootstrap (5–20 tasks), curating a real 50-task suite is a separate workstream. The framework supports it; the people-time to author tasks is the bottleneck.

Continued in [02-task-schema](./02-task-schema.md), [03-grading](./03-grading.md), [04-roadmap](./04-roadmap.md).
