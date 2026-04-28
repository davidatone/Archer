## Roadmap — bootstrap, not framework

**Status:** proposal · **See also:** [README](./README.md), [01-architecture](./01-architecture.md), [02-task-schema](./02-task-schema.md), [03-grading](./03-grading.md)

> **Plain-English summary.** Four slices, smallest first. Slice 1 (~1 week) builds a proper `archer eval run` command in C# — eval suites colocated next to agent YAMLs (`agents/code-scout.evals.yaml`), multi-agent comparison table, frozen repo snapshots, JSON output. Slice 2 (~1 week) adds robustness tests (interrupt / crash / missing file). Slice 3 (~3 days) hooks it into GitHub CI so a PR that makes the agent worse fails the build. Slice 4 is "write more questions" — that's people-time, not engineering. The original plan included a Python slice 0 as a throwaway prototype; we superseded it because the team is already a .NET shop and a single-file `dotnet run` script offered no real cost saving over committing to slice 1 directly.

The reviewer's argument that prompted this proposal is exactly right: *don't build framework surface before proving the agent works*. That argument applies recursively — **don't build eval framework surface before proving the eval works**. So the roadmap is deliberately stunted at the start: ship a tiny version that catches at least one real regression, then expand.

### Slice 0 — superseded

The original plan was a ~350-line Python script (`evals/run.py`) plus one task YAML — a throwaway prototype to validate the eval *idea* before committing to C#. We considered three implementation options and decided to skip the prototype slice entirely:

- **Python + bash:** rejected. Bash can't credibly call Azure OpenAI's Responses API for the judge; Python is foreign to a .NET shop.
- **Single-file C# script (`dotnet run evals/run.cs`):** plausible — .NET 10 supports it — but only saves one week vs. building slice 1 properly, while introducing a parallel implementation we'd then have to keep in sync.
- **Go straight to slice 1:** chosen. Slice 1's in-process design (calling `IArcherAgentGrain` directly rather than spawning a subprocess) is cleaner, more accurate, and fits the rest of the codebase. The "validate the idea cheaply" cost saving is illusory once you account for the migration churn.

The Python script and its task YAML at `evals/run.py` and `evals/tasks/001-find-grain-implementation.yaml` will be deleted when slice 1 ships. Until then they're left as a working reference implementation that proves the eval shape end-to-end.

---

### Slice 1 — `archer eval run` + matrix + JSON output (~1.5–2 weeks)

> Original estimate was "1 week". Bumped after a critic review flagged that fixture creation is on the critical path — graders need real `RunArtifacts` to verify against, so grader development can't start until at least one fixture mounts cleanly. Realistic split: 2 days fixture pipeline (tarball + sha256 + mount/extract/teardown + validation), 4 days runner + suite loader + cross-family validator, 3 days graders + judge + tests, 1 day result writer + console renderer + 10–15 task ports, 1–2 days polish + parity check.

**What this slice ships:**

- `src/Archer.Evals/` — new project. `EvalRunner`, `TrialExecutor`, `EvalSuiteLoader`, `RunArtifacts`.
- Graders: `BudgetGrader`, `CitationGrader`, `AntiCitationGrader`, `LexicalGrader`, `LlmJudgeGrader`. **All required**, per [03-grading](./03-grading.md).
- `src/Archer.Cli/Commands/EvalCommand.cs` — `archer eval run` (whole suite), `--task <id>` (one), `--output-json <path>`.
- **Colocated eval suites** — `agents/code-scout.evals.yaml`, `agents/critic.evals.yaml`, etc. The loader globs `agents/*.evals.yaml` (parallel to how the registry globs `agents/*.yaml`).
- 10–15 tasks total across 1–2 suites.
- `evals/fixtures/repos/archer-2026-04-27.fixture.yaml` + `.tar.gz` — first frozen fixture (`git archive HEAD` of the current repo, sha256 captured).
- Multiple agent ids in the matrix (target: `code-scout`, `code-scout-cheap`, `code-scout-no-reasoning`).
- New agent definitions: `agents/code-scout-cheap.yaml`, `agents/code-scout-no-reasoning.yaml` — distinct YAMLs (no `inherits:` yet).
- JSON result output, console table renderer.
- Tests in `tests/Archer.Evals.Tests/` — graders, suite loader, fixture mounting, multi-trial aggregation. *No agent runs in unit tests* — graders are pure functions over `RunArtifacts` so they're testable without spinning up the host. The judge grader is tested with a recorded transcript fixture, not a live API call.
- Migration: delete `evals/run.py`, `evals/tasks/`, and the Python `evals/README.md` once parity is confirmed.

**Done-when:**
- `archer eval run` produces a result table that matches reviewer expectations.
- The 3-agent × 10-task matrix runs in under 10 minutes total wall-time, under $5 in cost (judge usage included).
- Deterministic guardrails' verdicts agree with manual inspection 100% of the time on the bootstrap tasks.
- Judge verdicts agree with a careful human reading 100% of the time on the bootstrap tasks; disagreements force a rubric revision before merge.
- `archer eval run --task <id>` works as a focused dev loop (sub-1-min, single-task).
- Parity check: running the C# implementation against the slice-0 Python task produces the same verdict as the Python script (within stochasticity bounds — i.e., both at ≥80% pass-rate or both below).

**Open questions to resolve in this slice:**
- **Multi-trial: how many?** 5 is the starting default. The first runs against `code-scout` calibrate whether 5 distinguishes pass/fail or whether we need more.
- **Pass-rate threshold default.** 0.8 is the starting guess. Early data tells us if 0.7 or 0.9 fits better.
- **Anti-citation list format.** The schema lets you list explicit anti-paths; do we want a more general "any path under `src/Foo/` is wrong" predicate? Add only if a task needs it.
- **Concrete cross-family judge default.** GPT-5 agent → Claude-Sonnet-4 judge is the proposal's recommendation. Slice 1 makes the cross-family rule a load-time constraint; the *specific* judge model defaulted in suite-level YAML is decided when the first suite is authored. CI workflow needs Anthropic API credentials (or whichever cross-family provider is chosen) added alongside the existing Azure OpenAI ones.

**Risk:** the matrix concept is more useful in proposal than in practice. If after 15 tasks the cheap agent is uniformly worse and we never want to use it, the matrix dimension was wasted complexity. Mitigation: the schema makes a single-agent matrix the easy case (`agent-matrix: [{id: code-scout}]`).

---

### Slice 2 — robustness matrix + tool-call audit (1 week)

The reviewer's specific call-out: *restart/interruption cases*. This is the slice that proves Archer's architectural claims.

**Deliverables:**
- `RobustnessOrchestrator` — supports the four variant types from [02-task-schema § Robustness](./02-task-schema.md#robustness): `interrupt-at-iteration`, `kill-host-after-tool-calls`, `delete-file-during-mount`, `corrupt-file-during-mount`.
- `ToolCallAuditGrader` — required if slice 0/1 showed citations without grounding.
- `RobustnessVariantGrader` per variant type.
- Robustness variants added to 5+ existing tasks (mostly the citation-heavy ones, since those benefit most from "did you actually open the file?" checks).
- Console table now has a robustness row per cell.

**Done-when:**
- The interrupt-mid-turn variant of at least one task produces `TurnSupersededEvent` for the original turn AND a fresh response for the interrupting prompt; the verdict reflects this.
- `kill-host-after-tool-calls` proves state rehydration: kill, restart, continue, agent has its prior `Messages`.
- The `delete-file-during-mount` variant catches at least one case where the agent would otherwise hallucinate a missing file's contents.

**Why this slice matters more than it sounds:** without it, Archer's "durable, interruptible actors" claim is *architectural* rather than *empirical*. The reviewer specifically flagged that gap.

**Risk:** robustness grading is genuinely tricky — distinguishing "agent recovered gracefully" from "agent silently wrote a wrong answer that happens to match expectations" requires care. Mitigation: every robustness variant test is hand-verified once before being checked in.

---

### Slice 3 — CI integration + regression gate (3 days)

**Deliverables:**
- `.github/workflows/eval.yml` runs the suite on every PR using GitHub-hosted runners and `secrets.AZURE_OPENAI_*`.
- `archer eval pr-comment` formats the result table as a markdown comment that the workflow posts to the PR.
- `archer eval diff <run-id-a> <run-id-b>` — compares two result files.
- `--regression-baseline <ref>` flag — fetches the baseline run (cached as a GitHub Artifact from the latest `main` workflow), diffs cell-by-cell.
- Soft-fail (warn-only) for pass-rate drops 0.1 ≤ Δ < 0.2; hard-fail for Δ ≥ 0.2 or pass-rate falling below 0.6.
- Cost cap: workflow aborts if the running estimated cost exceeds `--max-cost $X` (default $5).

**Done-when:**
- A PR that breaks the agent's ability to cite a known file fails CI.
- A PR that improves things doesn't fail CI (the gate is *no-regression*, not *no-change*).
- CI run for a no-op PR (docs only, agent code unchanged) costs $0 because the runner caches results keyed by the touched file globs. (Stretch — add only if cost actually bites.)

**Risk:** flaky-test syndrome. If the suite fails 1-in-N times for non-real-regression reasons, developers learn to ignore it. Mitigation: the multi-trial pass-rate threshold IS the dampening mechanism; if it's not enough, raise `trials` for the noisiest tasks rather than weakening the gate.

---

### Slice 4 — task-quality investment (ongoing)

The reviewer's bar: *50 real repo-investigation tasks*. Reaching 50 isn't an engineering project — it's a *task-curation* project. The framework supports it; the throughput is people-time.

**Process:**
- Every PR that lands on `main` is required to either (a) leave the eval suite green or (b) add a task that catches the new behaviour. This forces the suite to grow with the codebase.
- A monthly "eval review" — for each task, look at trial samples; identify tasks the agents always pass (too easy → reduce or retire) or always fail (probably broken or impossibly hard → fix or retire); update fixtures when the underlying repo materially changes. Same review re-reads judge rubrics: a rubric that lets through wrong answers needs tightening; a rubric that fails right answers needs loosening.
- Tag tasks by domain (`citation`, `tracing`, `test-discovery`, `mcp-flow`, `regression`) so contributors can see coverage gaps.

**Done-when (target):**
- 50 tasks, total wall-time ≤ 30 minutes, total cost per run ≤ $5 (judge usage included).
- Per-task pass-rate distribution centred around 80–95% for `code-scout`; tasks below 50% trigger investigation.
- Cost-quality Pareto frontier readable in 30 seconds (graph in the markdown report).

This is a six-month effort, not a slice with a deadline.

---

### Cross-cutting concerns

| Concern | How it's handled across slices |
|---------|--------------------------------|
| **Cost runaway** | Per-trial budget (mandatory), per-run cap (`--max-cost`), per-cell cap, OTel meter. Judge cost is a separate line item from agent cost in every report. Slice 1 implements all three caps. |
| **Repository drift** | Fixtures are frozen tarballs with sha256. New fixture = new file + new task references. Old tasks keep working against old fixtures. (Slice 0 runs against the working copy as a deliberate shortcut; slice 1 introduces fixtures.) |
| **Model drift** | Pinned model deployments per agent definition. When a model version changes (Azure renames a deployment), the affected agent YAMLs need updating; pin the deployment string in the result JSON for forensic comparison. Same applies to the judge model — pin it, log it, and treat a judge model swap as a baseline-resetting change. |
| **Rolling pricing** | `Archer.Evals.Pricing` static table updated rarely; the cost figure in result JSONs is computed *at run time* using whatever the table said then; baseline diffs aren't poisoned by later table edits. |
| **Secret handling** | `AZURE_OPENAI_API_KEY` from CI secret only; never logged, never appears in result JSON. Same as today's CLI. |
| **Test pollution** | The runner's fixture mounts go to `~/.archer/eval-runs/<run-id>/`; teardown happens after grading. The temp dirs auto-clean after 7 days (cron-style background sweep) so disk doesn't grow. |
| **Observability** | OTel spans/metrics for every level (run/task/cell/trial). Aspire dashboard works. |

### Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Slice 0 finds the agent passes everything trivially | Low | Suite has no signal; framework wasted | Choose slice-0/1 tasks to *target known weakness* (e.g. distinguishing similarly-named files). If `code-scout` gets all 5 first try, write 5 harder ones. |
| Cost grows beyond useful | Medium | CI fails or is bypassed | Hard cap (`--max-cost`); mandatory `cost-tier` field on agent matrix entries; the cost-Pareto report makes expensive configs visible. Judge cost tracked separately so a runaway judge is visible distinctly from a runaway agent. |
| Tasks become brittle to prompt changes | Medium | Eval thrashes on every prompt edit | Predicates target *outcomes* (file paths, key terms) not phrasing. The judge is the lever for prompt-form sensitivity — its rubric assesses whether the answer is *correct*, not whether it phrases things a particular way. |
| Robustness graders mistake graceful failure for hallucination | Low | False fail | Hand-verify each robustness variant once before merging. |
| Multi-trial flakiness pollutes the gate | Medium | Distrust | `required-pass-rate` is the dial; raise it for high-stakes tasks. Don't weaken the gate, raise the trials. |
| Judge itself is stochastic and flips verdicts | Medium | False fail / false pass | Judge runs at `temperature: 0` with a structured-output rubric. Per-trial pass-rate aggregation means a one-off flaky judge is observable, not catastrophic. Judge ≠ subject model is enforced at task-load time. |
| Eval framework becomes more complex than the system it tests | Low (because we explicitly stop) | Maintenance burden | The roadmap *stops adding code* after slice 3. Slice 4 is task-authoring, not engineering. |

### Definition of "done" for the proposal

This is a proposal. Done = maintainers (you) read it, accept or reject the major design choices, and slice 1 begins. The Python prototype at `evals/run.py` proved the concept end-to-end and informs the C# implementation; it's deleted as part of slice 1.

The major design choices to accept/reject:

1. **Three pieces — task spec, runner, grader — with no other moving parts.** Strong recommendation: accept.
2. **Per-agent-config matrix as a first-class dimension.** Recommendation: accept; the cost/quality table is the headline output.
3. **Distinct `AgentDefinition` YAMLs for variants in v1; `inherits:` deferred.** Recommendation: accept.
4. **Multi-trial with `required-pass-rate`, never a single-trial verdict.** Strong recommendation: accept.
5. **Robustness as a first-class variant matrix (slice 2).** Strong recommendation: accept; it's the empirical proof of the architectural claims.
6. **LLM-as-judge is integral, not optional — every task declares a `judge:` block, every grading run includes a judge verdict.** Strong recommendation: accept. Deterministic guardrails are a cheap pre-filter, not a substitute for the judge.
7. **Judge ≠ subject model *and* ≠ subject model family, enforced at task-load time.** Strong recommendation: accept. Same-family pairings (e.g., gpt-5-codex judging gpt-5-mini) share training data and failure modes — they produce correlated false passes, which is the failure mode the judge is supposed to catch. Operational cost: cross-provider API credentials in CI (e.g., Azure OpenAI + Anthropic).
8. **Eval suites colocated with agent definitions (`agents/<id>.evals.yaml`), not in a separate `evals/tasks/` tree.** Strong recommendation: accept; reading the agent and its tests in adjacent files is the same ergonomics as test classes living next to the classes they test.
9. **Strict-subset rule for single-agent suites + a separate `agents/cross-agent.evals.yaml` file for genuinely cross-agent tasks (with a per-cell judge map).** Strong recommendation: accept. Without it, a suite's `defaults` block silently applies to non-primary agents and rubrics author themselves invisibly across agents.
10. **Skip the throwaway prototype slice; build slice 1 directly in C#.** Strong recommendation: accept (decided 2026-04-27).

### Cross-references

- [README](./README.md) — exec summary, philosophy, what the eval is *not*.
- [01-architecture](./01-architecture.md) — runner, fixtures, robustness, integration.
- [02-task-schema](./02-task-schema.md) — full YAML reference + worked example.
- [03-grading](./03-grading.md) — guardrails + judge, both required.
