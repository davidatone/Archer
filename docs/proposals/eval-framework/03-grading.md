## Grading — concrete graders and verdict aggregation

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [02-task-schema](./02-task-schema.md)

> **Plain-English summary.** This doc is about how a single answer becomes a PASS or FAIL. There are two layers, and an answer has to clear *both*. Layer one: cheap automatic checks ("did the answer mention the right file?", "did it stay under budget?"). Layer two: a second AI (the **judge**) reads the answer and decides if it's actually correct. The cheap checks catch obvious form errors. The judge catches "all the right words but wrong meaning" — which is the most common way LLMs fail. The judge is required for every question. To stop the judge from cheating, it has to be a different AI from the one being tested, it answers in a structured JSON format, it runs at zero temperature, and its reasoning is saved with every verdict.

This document defines how a trial moves from raw `RunArtifacts` (final answer + events + tokens + tool calls) to a pass/fail verdict, what each grader inspects, and how the LLM judge stays honest.

### Two layers — guardrails and judge — both required

A trial passes through cheap, deterministic **guardrails** *and* an **LLM judge**. Both must pass for the trial to pass. Guardrails grade the *form* of the answer (does it cite the right path, contain the right keyword, stay under budget); the judge grades the *substance* (does the answer correctly answer the question). Each catches a failure mode the other can't:

| Failure | Caught by | Not caught by |
|---------|-----------|---------------|
| Wrong file path | citation guardrail | judge alone (could miss it) |
| Hallucinated adjacent file | anti-citation guardrail | judge alone (might forgive close-enough names) |
| All-the-right-words but wrong substance | judge | guardrails alone |
| Confidently incorrect explanation | judge | any deterministic grader |
| Budget blowout | budget guardrail | judge (it doesn't see token counts) |

**The judge is not optional.** A guardrail-only eval is the reviewer's worst case ("a beautiful machine for producing mediocre artifacts that pass surface tests"). A judge-only eval is permissive (a generous judge passes weak answers). Both layers are needed and both are required for a trial to pass.

```
RunArtifacts
   │
   ├─> BudgetGrader            ┐
   ├─> CitationGrader          │  Guardrails — cheap, deterministic, fail-fast.
   ├─> AntiCitationGrader      │  All applicable guardrails must pass.
   ├─> LexicalGrader           │
   ├─> ToolCallAuditGrader     ┘
   │
   ├─> RobustnessVariantGrader (only when robustness != baseline)
   │
   └─> LlmJudgeGrader          ALWAYS RUNS. Must pass.
                                   │
                                   ▼
                              TrialVerdict { pass iff every grader passed }
```

The order is **deterministic-cheap-first** for fast diagnostics, but the judge always runs even if a guardrail already failed — surface and substance failures are independent and we want both signals in the report. Cell aggregation: `cell.pass-rate = passed / trials`; cell passes if `pass-rate >= required-pass-rate`.

Each grader is a class implementing:

```csharp
public interface IGrader
{
    string Id { get; }
    bool IsApplicable(TaskSpec task, TrialContext trial);
    GraderVerdict Grade(TaskSpec task, TrialContext trial, RunArtifacts run);
}

public sealed record GraderVerdict
{
    public required string GraderId;
    public required bool Passed;
    public required string Summary;             // one-line; for the table renderer
    public IReadOnlyDictionary<string, object>? Details;   // structured payload for the JSON
}
```

Graders are pure: same artefacts in, same verdict out. They have no I/O, no model calls (with one exception — see LlmJudge). They live in `src/Archer.Evals/Grading/`.

### BudgetGrader

Reads token counts and durations from `RunArtifacts`, compares against `task.budget.per-trial`. Pass = every cap respected.

```csharp
public GraderVerdict Grade(TaskSpec task, TrialContext trial, RunArtifacts run)
{
    var budget = task.Budget?.PerTrial ?? trial.SuiteDefaults.Budget.PerTrial;
    var failures = new List<string>();
    if (run.OutputTokens > budget.MaxOutputTokens)  failures.Add($"output tokens {run.OutputTokens} > {budget.MaxOutputTokens}");
    if (run.InputTokens > budget.MaxInputTokens)    failures.Add(...);
    if (run.ToolCalls.Count > budget.MaxToolCalls)  failures.Add(...);
    if (run.WallTime > budget.MaxWallSeconds)       failures.Add(...);
    if (EstimatedCost(run) > budget.MaxCostUsd)     failures.Add(...);

    return failures.Count == 0
        ? GraderVerdict.Pass("budget", $"all caps respected: {run.OutputTokens}/{budget.MaxOutputTokens} tok, {run.WallTime.TotalSeconds:F1}s")
        : GraderVerdict.Fail("budget", string.Join("; ", failures));
}
```

A trial that exhausted its budget *during execution* (the runner's enforcer killed it) automatically fails the budget grader, but other graders still run — you might see "got the right citation but burned 2× tokens" rather than a single opaque "fail".

### CitationGrader

Parses path-shaped strings out of the final answer; intersects with `must-cite-files`.

```csharp
private static readonly Regex PathPattern = new(
    @"(?:^|[\s`'""])((?:[a-zA-Z0-9_\-./]+/)+[a-zA-Z0-9_\-.]+\.(?:cs|md|yaml|yml|json|csproj|slnx|props|targets))(?:[\s`'"":,;]|$)",
    RegexOptions.Compiled);

public GraderVerdict Grade(...)
{
    var cited = PathPattern.Matches(run.FinalAnswer)
        .Select(m => m.Groups[1].Value.Replace('\\', '/'))
        .ToHashSet(StringComparer.Ordinal);

    var missing = task.Expected.MustCiteFiles
        .Where(expected => !cited.Any(c => c.EndsWith(expected, StringComparison.Ordinal)))
        .ToList();

    return missing.Count == 0
        ? GraderVerdict.Pass("citation", $"cited all {task.Expected.MustCiteFiles.Count} expected files")
        : GraderVerdict.Fail("citation", $"missing: {string.Join(", ", missing)}");
}
```

Two notes:

1. **Suffix match** — the agent might cite `archer/src/Archer.Actors/Grains/ArcherAgentGrain.cs` while the expected was `src/Archer.Actors/Grains/ArcherAgentGrain.cs`. Suffix-matching handles repo-prefix variation without false positives.
2. **`must-cite-files-with-line-range`** is a separate grader (`CitationLineRangeGrader`) that requires the path *and* a line number within ±2 of the expected line. Used when "the right file" isn't enough — you wanted the right method.

### AntiCitationGrader

Inverse of citation: `expected.must-not-cite-files`. Pass iff none of those paths appear. This is the explicit hallucination guard — a citation grader alone says "the right file is named", but doesn't catch "the right file is named *and so is a wrong-but-plausible neighbour*".

For the worked example task in [02-task-schema](./02-task-schema.md#worked-example), the anti-citations are:
- `src/Archer.Actors/Grains/TurnWorkerGrain.cs` (adjacent file the agent could conflate)
- `src/Archer.Application/Agents/IAgentDefinitionRegistry.cs` (similar name, different concern)

### LexicalGrader

Substring/whole-word matching for `must-mention` / `must-not-mention`. Configurable case-sensitivity and word-boundary mode (per task; see [02-task-schema § Grading](./02-task-schema.md#grading)).

```csharp
private bool Contains(string haystack, string needle, MatchMode mode, bool caseSensitive)
{
    var compare = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    return mode switch
    {
        MatchMode.Substring => haystack.Contains(needle, compare),
        MatchMode.Word => Regex.IsMatch(haystack,
            $@"\b{Regex.Escape(needle)}\b",
            caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
```

Defaults: word mode, case-insensitive. "Word" prevents `AgentState` from matching `AgentStateStore` and similar near-misses.

### ToolCallAuditGrader

Walks `RunArtifacts.Events` looking for `ToolCallStartedEvent`s that match the task's `must-call-tool-on-files` predicates. This is the answer to "did the agent actually look at the file before citing it?" — defends against the failure mode where the model invents a plausible-sounding citation without grounding.

```yaml
must-call-tool-on-files:
  - tool: list_files
    file-glob: "src/Archer.Actors/**"
  - tool: grep
    pattern-contains: "ArcherAgentGrain"
```

A predicate matches if at least one `ToolCallStartedEvent` has the right `ToolName` and the right structural shape:
- `file-glob` → arguments include a `path` matching the glob (or starting from a parent that does)
- `pattern-contains` → arguments include a `pattern` string containing the listed substring
- `arg-equals` → exact key-value match on a single tool argument

### RobustnessVariantGrader

Only runs when the trial's robustness variant ≠ `baseline`. The variant `type` selects a specialised check. Examples (full set in [02-task-schema § Robustness](./02-task-schema.md#robustness)):

- **`interrupt-at-iteration`** — the events log must contain a `TurnSupersededEvent` for the original turn id, AND a fresh `TurnStartedEvent` for the user's interrupt prompt, AND that fresh turn must terminate cleanly. *Pass criterion: the fence works.*
- **`kill-host-after-tool-calls`** — the runner SIGTERMs the host after N tool calls, restarts it, sends a continuation prompt. Pass = the agent's pre-kill `Messages` are still present in state, the continuation produces a coherent response. *Pass criterion: durability works.*
- **`delete-file-during-mount`** — fixture is mounted with a file deleted. Pass = anti-citation passes (no hallucinated file content) AND the agent's final answer either reports inability or proceeds without the missing context. *Pass criterion: no hallucination.*

These graders are how Archer's architectural claims (fenced turns, durable state, no-hallucination guards) get *empirical* backing.

### LlmJudgeGrader — required for every task

The judge is the only grader that can detect a substantively-wrong answer that happened to use the right keywords. It runs on every trial. The task spec only declares the *rubric* (what counts as correct in this domain) and the *judge model*; it doesn't declare whether the judge runs — that's not optional.

```yaml
judge:
  model: gpt-5-codex                    # judge model (must differ from agent's model)
  rubric: |
    The agent was asked to locate ArcherAgentGrain and describe three of its
    methods. Score 0-5 considering:
    - Accuracy: does the cited file path actually contain the class?
    - Method coverage: are all three methods correctly described?
    - Precision: any hallucinated APIs or files that don't exist?
    - Conciseness bonus: subtract 1 if the response is rambling.
    Pass if final score >= 4. Output JSON only:
    {"score": <int 0-5>, "verdict": "pass"|"fail", "reasoning": "<one paragraph>"}
  pass-at: 4
```

Implementation:

```csharp
public sealed class LlmJudgeGrader : IGrader
{
    private readonly IChatClientFactory _chatClientFactory;

    public async Task<GraderVerdict> GradeAsync(
        TaskSpec task, TrialContext trial, RunArtifacts run, CancellationToken ct)
    {
        // Judge config (model + rubric + pass-at) is per-cell when the suite is
        // agents/cross-agent.evals.yaml, otherwise per-task.
        var judgeConfig = task.ResolveJudgeFor(trial.AgentId);

        // No runtime guard for judge ≠ subject or judge family ≠ subject family —
        // load-time validation in EvalSuiteLoader has already rejected violations.
        // Running this re-check at grade time would be defensive duplication.

        var prompt = BuildJudgePrompt(judgeConfig.Rubric, task, run);  // rubric + agent prompt + agent answer
        var client = _chatClientFactory.Create(judgeConfig.Model);
        var response = await client.GetResponseAsync(
            prompt, new ChatOptions { Temperature = 0 }, ct);
        var parsed = ParseStructuredVerdict(response);                 // {score, verdict, reasoning}

        return parsed.Score >= judgeConfig.PassAt
            ? GraderVerdict.Pass("judge", $"score={parsed.Score} — {parsed.Reasoning}")
            : GraderVerdict.Fail("judge", $"score={parsed.Score} ({judgeConfig.PassAt} required) — {parsed.Reasoning}");
    }
}
```

Note: `task.ResolveJudgeFor(agentId)` returns the per-cell judge for cross-agent tasks (where `judge.by-agent: { ... }` is required) and the single per-task judge otherwise. The grader doesn't know or care which form the YAML used.

Constraints that keep the judge honest:

1. **Judge ≠ subject (model name *and* family).** Enforced at task-load time by `EvalSuiteLoader`; the runner refuses to start a suite that violates either rule. Family is a static mapping (gpt-5, o-series, claude-4, claude-3, gemini-2 — see [02-task-schema § Cross-family judge](./02-task-schema.md#cross-family-judge)). Same-name catches the obvious case (`gpt-5-codex` judging `gpt-5-codex`); same-family catches the dangerous case (`gpt-5-codex` judging `gpt-5-mini` — different names, shared training data, correlated false passes).
2. **Structured output.** The rubric ends with the JSON shape (`{"score", "verdict", "reasoning"}`). Free text is re-prompted once; if still malformed the trial is recorded with `judge-output-malformed` and counts as a fail.
3. **`temperature: 0`** in the judge call. The judge is itself a model and itself stochastic — temperature 0 with structured output dampens that.
4. **Reasoning logged.** Every judge verdict carries its `reasoning` text into the result JSON. A wrong call (judge passed when humans wouldn't) is reviewable; a stricter rubric is the response.
5. **Separate cost meter.** Judge calls roll into a distinct `archer.eval.judge.cost-usd` metric so you can see what the judge dimension costs vs. the agent.
6. **Per-trial budget covers the judge call.** The judge counts toward `budget.max-cost-usd`; an over-budget trial fails the budget grader regardless of judge verdict.

#### What about cheap-to-grade tasks?

Some tasks have such crisp criteria (citation only, exact method name) that the deterministic guardrails are arguably sufficient and a judge feels redundant. The proposal still requires the judge for every task, for two reasons:

- **The judge catches what guardrails can't.** Even on simple "find the file" tasks, the agent can cite the right file with a wrong explanation of what's in it. The judge sees both.
- **Uniformity simplifies the harness.** Every trial follows the same pipeline; we don't grow conditional branches for "judge-eligible vs not." When you decide a task is too crisp for a judge, the right move is to delete the task — its information value is too low to justify being in the suite.

#### Cost trade-off, plainly

Judging adds approximately one extra agent-turn-equivalent of cost per trial. For our typical bootstrap task (~10k input + ~5k output tokens for the agent, ~4k input + ~300 output for the judge), the judge is roughly 30–40% extra cost. That's the price of measuring substance, not just surface. The cost reports surface it as a separate line so you know what you're paying for.

### Cell-level aggregation

A cell is `(task, agent, robustness)`. Each cell runs `task.trials` trials and each trial produces a `TrialVerdict`. The cell verdict:

```csharp
public sealed record CellVerdict
{
    public required int Trials;
    public required int Passed;
    public required double PassRate;          // Passed / Trials
    public required bool CellPassed;          // PassRate >= task.RequiredPassRate
    public required CellSummary Summary;      // mean tokens, cost, time, exhaustion reasons
    public required IReadOnlyList<TrialVerdict> TrialDetails;
}
```

Aggregations the runner computes on top:

- **Per-task pass-rate** — averaged across the matrix entries. Useful for "is this task even feasible with our current agents?"
- **Per-agent pass-rate** — averaged across tasks. The headline of the cost-quality table.
- **Cost-quality Pareto** — for each `(task, agent)`, the `(estimated-cost, pass-rate)` point. Plot a frontier; identifies dominated configs ("never use code-scout-no-reasoning, it's worse than code-scout-cheap on every axis").
- **Regression** — diff against a baseline run (typically `origin/main`). For each cell, classify as `improved | stable | regressed | new | removed`. The CI gate trips on `regressed && new-pass-rate < 0.6`.

### Reporting

Two output forms.

1. **JSON** — full record, `evals/results/<run-id>.json`. Schema in [01-architecture § Result schema](./01-architecture.md#result-schema). Permanent, archived per run, gitignored.
2. **Console / markdown table** — what humans look at. Generated from the JSON, fits in a terminal:

```
Eval run eval_2026-04-27T09-31-12Z   (sha ff6baa4)

Task                     │ code-scout            │ code-scout-cheap      │ code-scout-no-reas
─────────────────────────┼───────────────────────┼───────────────────────┼──────────────────────
001-find-grain-impl      │ ✓ 5/5 · 18.2k · 47s  │ ✓ 5/5 · 4.2k · 12s   │ ✗ 3/5 · 2.1k · 6s
002-trace-event-flow     │ ✓ 5/5 · 24.1k · 62s  │ ✓ 4/5 · 7.8k · 28s   │ ✗ 1/5 · 3.0k · 9s
003-find-fence-fail-mode │ ✓ 5/5 · 19.0k · 51s  │ ✗ 2/5 · 5.1k · 18s   │ ✗ 0/5 · 1.8k · 5s
─────────────────────────┼───────────────────────┼───────────────────────┼──────────────────────
Tasks passed             │  3/3 (100%)          │  2/3 (67%)            │  0/3 (0%)
Mean cost / task         │  $0.18                │  $0.05                │  $0.02
Mean wall / task         │  53s                  │  19s                  │   7s

Robustness (all agents): interrupt-mid-turn ✓ 14/15 · kill-host-mid-trial ✓ 15/15 · missing-file ✓ 13/15
Total cost: $1.42 · Total time: 5m 28s · Cache hit: 0%
```

The same data renders to a markdown table for PR comments.

### Failure modes the grader pipeline is designed against

| Failure | Grader / mechanism that catches it |
|---------|------------------------------------|
| Agent invents a file path that *looks* right | `AntiCitationGrader` lists known plausible-but-wrong neighbours |
| Agent cites the right file but didn't actually open it | `ToolCallAuditGrader` checks for the corresponding `ToolCallStartedEvent` |
| Agent passes once by luck | Multi-trial + `required-pass-rate` |
| Agent over-spends to get the right answer | `BudgetGrader` (separate from correctness) — pass-with-warning |
| Agent breaks under interrupt | `RobustnessVariantGrader` for `interrupt-at-iteration` |
| Agent silently regresses on a previously-good task | Baseline diff in CI; soft-fail at 0.1 drop, hard-fail at 0.2 |
| Judge model rubber-stamps obvious failures | Judge model required to differ from subject; full reasoning logged |
| Suite passes but costs 10× as much as before | Cost-Pareto report; per-run `--max-cost` cap; OTel `archer.eval.run.cost` metric |

### What a "passing trial" really means

A passing trial means: **for this single trial, the agent produced an answer that satisfies the deterministic predicates we wrote down, didn't waste budget beyond the cap, and (if used) wasn't called out by a separately-configured judge model.**

What it doesn't mean:

- The answer is the *best* possible answer.
- The answer would satisfy a human reader for a different but related question.
- The agent will pass again tomorrow on the same fixture (LLM drift; pass-rate is the truth).

The eval system's job is to detect *regression*, not to certify *fitness*. The bar moves up as task quality improves; the gate exists to make sure it doesn't move down silently.

### Cross-references

- [01-architecture](./01-architecture.md) — runner, fixtures, robustness orchestration
- [02-task-schema](./02-task-schema.md) — task YAML reference and worked example
- [04-roadmap](./04-roadmap.md) — bootstrap path, when each grader lands
