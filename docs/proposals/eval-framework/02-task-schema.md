## Eval task schema

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [03-grading](./03-grading.md)

> **Plain-English summary.** This doc is the recipe-card for writing eval questions. Eval tasks live *next to* the agent they target — `agents/code-scout.yaml` is the agent, `agents/code-scout.evals.yaml` is its eval suite. One file holds many tasks (like a test class holds many test methods). Each task says: what to prompt, what files the answer must mention, what files it must NOT mention, what keywords to look for, what the budget caps are, and the rubric the judge AI grades against. Every task must declare a judge — there's no "skip the judge" option. There's a worked example at the bottom of this doc; copy-paste from there.

### File layout — colocation

Eval suites live alongside the agent definitions they target:

```
agents/
├── code-scout.yaml              ← the agent
├── code-scout.evals.yaml        ← all eval tasks targeting code-scout
├── critic.yaml
├── critic.evals.yaml
└── …

evals/
├── fixtures/repos/              ← frozen-repo tarballs (still top-level)
└── results/                     ← gitignored run output
```

The `.evals.yaml` lives next to the agent it tests. Discovery is `agents/*.evals.yaml`.

#### Strict-subset rule (single-agent-family suites)

Tasks inside `agents/<id>.evals.yaml` are restricted to *variants of `<id>`*. Concretely: every entry in a task's `agent-matrix` must have an id that starts with `<id>` (so `code-scout`, `code-scout-cheap`, `code-scout-no-reasoning` are all allowed in `code-scout.evals.yaml`; `critic` is not). The loader rejects suites that violate this with a clear error.

This avoids a silent failure mode: without the rule, a suite's `defaults` block (especially `judge.model`) would invisibly apply to whichever agents happen to appear in `agent-matrix`, and the rubric (authored for one agent's typical output) would silently grade a different agent.

#### Cross-agent tasks live in `agents/cross-agent.evals.yaml`

When a task genuinely compares unrelated agents (e.g., does `critic` agree with `code-scout` on the same prompt?), it lives in `agents/cross-agent.evals.yaml`. That file is exempt from the strict-subset rule, but every task in it must declare a per-cell judge map:

```yaml
# agents/cross-agent.evals.yaml — task entry
judge:
  by-agent:
    code-scout:    { model: claude-sonnet-4-6, rubric: "..." }
    critic:        { model: claude-sonnet-4-6, rubric: "..." }
    code-scout-cheap: { model: claude-sonnet-4-6, rubric: "..." }
  pass-at: 4
```

The loader rejects a `cross-agent.evals.yaml` task that uses the single-agent `judge: { model, rubric }` form — the per-cell map is required there.

This keeps the simple case simple (single-agent suites use the simple `judge:` form) and forces the cross-agent case to make its judge/rubric choices explicit per agent.

The runner loads suites via `IEvalTaskLoader`, parallel to how `IAgentDefinitionRegistry` loads `agents/*.yaml`. Hot reload isn't needed (evals are CLI-invoked, not long-lived).

This doc is the schema reference. A worked example is at the bottom.

### Suite top level (the `.evals.yaml` file)

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `version` | string | yes | — | Schema version pin. Loader rejects unknowns. Currently `"1"`. |
| `defaults` | mapping | no | `{}` | Suite-level defaults shallow-merged into each task. Common place to declare `judge.model`, `trials`, `required-pass-rate`. |
| `tasks` | list | yes | — | One or more tasks. Each entry is the per-task schema below. |

#### Suite defaults

```yaml
# agents/code-scout.evals.yaml
version: "1"

defaults:
  trials: 5
  required-pass-rate: 0.8
  budget:
    per-trial:
      max-output-tokens: 50000
      max-wall-seconds: 120
      max-tool-calls: 30
  judge:
    model: gpt-5-codex                # judge ≠ subject — enforced at load time
    pass-at: 4

tasks:
  - id: find-grain-implementation
    …
  - id: trace-tool-call-flow
    …
```

A task can override any defaulted field individually (shallow merge — task `judge.rubric` doesn't replace the suite's `judge.model`).

### Task top level (entries inside `tasks:`)

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `id` | string | yes | — | Globally unique across all `.evals.yaml` files. `kebab-case`. The id, not the filename, is what shows up in result tables and CI comments. |
| `description` | string | no | `""` | Human-readable. Surfaced in CLI lists and PR comments. |
| `tags` | list | no | `[]` | Labels for filtering (`citation`, `tracing`, `regression`, …). |
| `prompt` | string | yes | — | The user message sent to the agent. Multi-line YAML strings welcome. |
| `fixture` | string | yes | — | Fixture id — references `evals/fixtures/repos/<id>.tar.gz`. See [§ Fixture](#fixture). |
| `agent-matrix` | list | yes | — | At least one agent id. See [§ Agent matrix](#agent-matrix). |
| `expected` | mapping | yes | — | What "right" looks like. See [§ Expected](#expected). |
| `budget` | mapping | no | suite defaults | Per-trial caps. See [§ Budget](#budget). |
| `trials` | int | no | `5` | How many trials per cell. |
| `required-pass-rate` | float | no | `0.8` | A cell passes when `passed / trials >= this`. |
| `robustness` | list | no | `[{id: baseline}]` | Variants beyond baseline. See [§ Robustness](#robustness). |
| `grading` | mapping | no | sensible defaults | Override per-grader thresholds. See [§ Grading](#grading). |
| `judge` | mapping | yes (after defaults) | — | Required for every task. See [§ Judge](#judge). |

The suite's `defaults` provide values for any of the above keys; the per-task block overrides field-by-field.

### Fixture

A frozen-repo tarball that mounts as the agent's workspace. Tasks reference it by id; the fixture metadata (path, sha256) lives once in `evals/fixtures/repos/<id>.fixture.yaml` so multiple tasks can share the same tarball without repeating themselves.

```yaml
# evals/fixtures/repos/archer-2026-04-27.fixture.yaml
id: archer-2026-04-27
source: archer-2026-04-27.tar.gz                    # relative to evals/fixtures/repos/
sha256: a1b2c3...                                   # tamper detect; runner aborts on mismatch
git-sha: ff6baa4                                    # informational
mount-as: "."                                       # subdirectory under trial root, default "."
```

```yaml
# in a task
fixture: archer-2026-04-27                          # references the file above
```

For multi-repo workflows (e.g. testing the future workflow runtime described in `docs/proposals/multi-agent-sdlc/`), `fixture` becomes `fixtures: [id1, id2]` with the same shape per entry. v1 is single-repo; multi-repo lands when the workflow runtime does.

### Agent matrix

The dimension that makes evals more than "did this prompt work once?". Each entry produces an evaluation cell.

```yaml
agent-matrix:
  - id: code-scout
    note: production default
  - id: code-scout-cheap
    note: gpt-5-mini + reasoning=low
    cost-tier: low                # informational; surfaced in reports
  - id: code-scout-no-reasoning
    note: ablation control
    cost-tier: cheap
```

| Field | Notes |
|-------|-------|
| `id` | Must resolve in `IAgentDefinitionRegistry`. Eval fails to load if not found. |
| `note` | Free-text. Renders in result tables next to the agent id. |
| `cost-tier` | Optional `cheap` / `low` / `default` / `high`. Used by `archer eval show --by-cost`. |
| `override` (future) | Reserved for inline patches to the resolved `AgentDefinition`. v1 = use distinct YAMLs in `agents/`. |

#### Why distinct YAMLs first

`code-scout`, `code-scout-cheap`, `code-scout-no-reasoning` are three full `AgentDefinition` files in `agents/`. Each is a normal definition the registry already knows how to load. Pros:

- The registry doesn't need a new "inheritance" feature.
- Each definition is self-contained — readers see exactly what's wired.
- Eval results pin themselves to immutable agent ids; "cheap" today and "cheap" in six months are reproducibly distinct.

The cost: when you tweak the prompt for `code-scout`, you have to remember to mirror it to `code-scout-cheap`. That's annoying, but it's also forcing function — cheap-variant prompt drift is a real failure mode worth noticing.

If we end up with > 4 variants we'll add `inherits:` (see roadmap).

### Expected

The set of correctness predicates the trial is graded against. All are optional individually, but the task must declare *at least one* (a task with no predicates is a no-op).

```yaml
expected:
  must-cite-files:
    - src/Archer.Actors/Grains/ArcherAgentGrain.cs
  must-cite-files-with-line-range:
    - { file: src/Archer.Domain/Agents/AgentState.cs, line: 18 }   # the IsTurnActive method
  must-not-cite-files:
    - src/Archer.Actors/Grains/TurnWorkerGrain.cs                 # adjacent, easy hallucination
  must-mention:
    - InitializeAsync
    - AddUserMessageAsync
  must-not-mention:
    - "Microsoft.Orleans.Streams"                                  # we don't use streams; mention = hallucination
  must-call-tool-on-files:
    - tool: list_files
      file-glob: src/Archer.Actors/**
    - tool: grep
      pattern-contains: "ActiveTurnId"
  must-not-error: true                                              # final-answer must not be a TurnFailedEvent
```

| Field | Pass condition |
|-------|----------------|
| `must-cite-files` | Every listed file appears as a path-shaped string in the final answer. |
| `must-cite-files-with-line-range` | The cited file appears with a line number ±2 of the listed line. |
| `must-not-cite-files` | None of these appear. Catches hallucinations of plausible-looking neighbours. |
| `must-mention` | Every term appears as a whole-word match in the final answer. Case-sensitive by default. |
| `must-not-mention` | None of these appear. |
| `must-call-tool-on-files` | The events log contains `ToolCallStartedEvent`s matching the tool + path/pattern criteria. |
| `must-not-error` | No `TurnFailedEvent` for the trial's turn. |

These are the deterministic graders ([03-grading](./03-grading.md)). Fuzzy "did the answer make sense" goes through the LLM judge, declared separately.

### Budget

```yaml
budget:
  per-trial:
    max-output-tokens: 50000
    max-input-tokens: 200000           # prompt + tool results sent to the model, summed over turns
    max-tool-calls: 30
    max-turns: 10
    max-wall-seconds: 120
    max-cost-usd: 0.50                 # estimated from token counts × pricing table
```

If `budget` is omitted, suite-level defaults from `evals/eval-suite.yaml` apply.

When a budget is exhausted the runner calls `agent.InterruptAsync(...)`. The trial then proceeds to grading; budget exhaustion *automatically fails* the budget grader but other graders still run (so you can see "got the right citation but burned 2× the token budget" rather than just "fail").

### Trials and stochasticity

```yaml
trials: 5                              # default
required-pass-rate: 0.8                # cell passes if 4/5 succeed; default 0.8

# Optional knobs to dampen stochasticity (only applied to agent definitions used in this task):
seed: 42                               # if model supports
temperature: 0
```

Stochasticity is the central problem of agent evals. The defaults — 5 trials, pass at 80% — give the runner enough samples to distinguish "this works" from "this works sometimes". For high-stakes regression gates you can ratchet up:

```yaml
trials: 10
required-pass-rate: 0.9
```

Cost grows linearly; spend it where it matters.

### Robustness

Variants beyond `baseline`. Each runs as its own row in the cell × variant matrix. Variant-specific graders override the baseline ones where applicable.

```yaml
robustness:
  - id: baseline                       # implicit; no need to declare unless you want to skip it via 'enabled: false'

  - id: interrupt-mid-turn
    type: interrupt-at-iteration
    iteration: 2                       # send agent.InterruptAsync after the 2nd model response
    grader-overrides:
      must-supersede-active-turn: true # cell's events must include TurnSupersededEvent

  - id: kill-host-after-tool-calls
    type: kill-host-after-tool-calls
    after-tool-calls: 3
    expect-resume-on-restart: true     # restart host; assert the agent's state survives

  - id: missing-file
    type: delete-file-during-mount
    file: src/Archer.Domain/Agents/AgentMessage.cs
    grader-overrides:
      must-not-cite-files:
        - src/Archer.Domain/Agents/AgentMessage.cs        # no hallucination of the deleted file
      must-mention:
        - "not found"                                      # graceful failure language
```

Variant types reserved for v1:

| `type` | Description |
|--------|-------------|
| `interrupt-at-iteration` | Send `InterruptAsync` after the Nth model response. Tests turn fencing. |
| `kill-host-after-tool-calls` | SIGTERM the host after N tool calls; restart; agent state must rehydrate. Tests durability. |
| `delete-file-during-mount` | Delete a file during fixture mount. Tests graceful-failure / no-hallucination. |
| `corrupt-file-during-mount` | Replace file content with garbage. Tests that the agent doesn't make stuff up. |
| `latency-injection` (future) | Wrap MCP/tool calls with synthetic delay; measure if budget caps still hold. |

Variants compose with the agent matrix: a task with 3 agents × 4 variants × 5 trials = 60 trials.

### Grading

Per-task tweaks for the grader thresholds. Most tasks should leave this alone — the suite defaults are calibrated.

```yaml
grading:
  citation:
    require-all-listed: true           # default; vs. require-any
  lexical:
    case-sensitive: false              # default true
    match-mode: word                   # word | substring
```

The judge is declared at the top level of the task (not under `grading`) because it's not optional — every task must specify it. See [§ Judge](#judge) below.

### Judge

Every task ships an LLM judge with a rubric. The judge grades the *substance* of the answer (does it actually answer the question correctly?) — guardrails grade the *form* (does the answer mention the right strings?). Both layers must pass for the trial to pass; see [03-grading](./03-grading.md) for why the judge is integral, not optional.

```yaml
judge:
  model: claude-sonnet-4-6              # required; must differ from every agent's model AND family
  rubric: |
    The agent was asked: "Where is ArcherAgentGrain implemented and what
    do its core methods do?". Score 0-5 considering:
    - Accuracy: does the cited file path actually contain the class?
    - Method coverage: are all three methods correctly summarised?
    - Precision: any hallucinated APIs or files?
    Pass if final score >= 4. Output JSON only:
    {"score": <int 0-5>, "verdict": "pass"|"fail", "reasoning": "<one paragraph>"}
  pass-at: 4                            # default; the score the judge must meet for verdict=pass
```

| Field | Required | Notes |
|-------|----------|-------|
| `model` | yes | Judge model. Validated at load time to differ in *both* model name and model family from every agent in `agent-matrix`. See [§ Cross-family judge](#cross-family-judge). |
| `rubric` | yes | Domain-specific grading instructions. *Must* end with the JSON output spec — the runner re-prompts once on malformed output, then fails the trial as `judge-output-malformed`. |
| `pass-at` | no, default 4 | The numeric threshold the judge's `score` must meet. Lower for permissive tasks, higher for strict ones. |

For cross-agent suites (`agents/cross-agent.evals.yaml`), the simple `judge: { model, rubric, pass-at }` form is rejected — those tasks must declare `judge.by-agent: { <agent-id>: { model, rubric }, ... }` so the judge / rubric is explicit per cell.

### Cross-family judge

A judge model must come from a *different model family* than every agent in the matrix — not just a different model name. Reason: a judge that shares training data and failure modes with the subject produces correlated false passes. The eval looks green precisely when the subject is wrong in ways the judge is also wrong about — the case the reviewer was most worried about.

Family is determined by a static mapping in `Archer.Evals` (extend as new providers land):

| Family | Models |
|--------|--------|
| `gpt-5` | `gpt-5`, `gpt-5-codex`, `gpt-5-mini`, `gpt-5-nano`, any string starting with `gpt-5` |
| `o-series` | `o1`, `o1-mini`, `o3`, `o3-mini`, `o4-mini`, any string starting with `o` followed by a digit |
| `claude-4` | `claude-opus-4-*`, `claude-sonnet-4-*`, `claude-haiku-4-*` |
| `claude-3` | `claude-3-*` (deprecated; flagged on use) |
| `gemini-2` | `gemini-2-*` |

The validator extracts the family from the `judge.model` and from each agent's resolved model. If they overlap, the task is rejected with a message naming both models:

```
ERROR: agents/code-scout.evals.yaml task 'find-grain-implementation':
  judge.model='gpt-5-codex' is in family 'gpt-5'
  agent 'code-scout' uses model 'gpt-5-codex' which is in family 'gpt-5'
  cross-family is required — pick a judge from a different family
  (e.g., claude-sonnet-4-6, gemini-2-pro)
```

Practical pairings for an Archer agent that uses GPT-5:

- **GPT-5 subject + Claude-4 judge** — recommended default.
- **Claude-4 subject + GPT-5 judge** — also fine.
- **GPT-5 subject + Gemini-2 judge** — fine but less common; cost/latency similar.

Practical pairings to **avoid**:

- GPT-5-codex subject + GPT-5-mini judge — same family.
- Claude-sonnet-4-6 subject + Claude-opus-4-* judge — same family.
- Aliased deployments of the same underlying model (e.g., `gpt-5-codex` vs `gpt-5-codex-2026-03`) — the validator flattens deployment suffixes when comparing.

The check is operational, not free: pairing GPT-family agents with Claude-family judges means the eval CI workflow needs Anthropic API credentials in addition to Azure OpenAI ones. This is a stated cost of the approach.

### Validation (loader-time)

Tasks that fail to load are reported and the suite refuses to start until they're fixed. Errors:

- duplicate task `id` across all `agents/*.evals.yaml` files
- `agent-matrix` referencing an unknown agent id
- `fixture` doesn't resolve to a `evals/fixtures/repos/<id>.fixture.yaml`
- fixture's on-disk tarball sha256 doesn't match the recorded value
- `expected` block contains no predicates
- **`judge` block missing or `judge.rubric` empty** (after defaults applied) — the judge is integral, not optional
- **`judge.model` resolves to the same model as *any* agent in `agent-matrix`** (judge ≠ subject is required for every cell)
- **`judge.model` is in the same model family as *any* agent in `agent-matrix`** (cross-family is required — see § Cross-family judge below)
- **single-agent suite has a task whose `agent-matrix` contains an id outside the `<id>-*` namespace** (strict-subset rule for `agents/<id>.evals.yaml`)
- **`agents/cross-agent.evals.yaml` task uses the single-agent `judge:` form** instead of the required `judge.by-agent` per-cell map
- `must-cite-files-with-line-range[].file` doesn't exist in the fixture (run-time check on first trial)
- `robustness[].iteration` ≤ 0 or > `budget.max-turns`
- `trials` < 1 or `required-pass-rate` not in `(0, 1]`

### Per-suite vs. per-task vs. CLI override

Three layers, deepest-wins:

1. **Suite `defaults`** in the `.evals.yaml` file — applies to every task in the suite.
2. **Per-task block** — overrides any defaulted field shallow-style (a task `judge.rubric` doesn't replace the suite's `judge.model`).
3. **CLI flags** — `--max-cost-per-trial 0.20`, `--trials 10` etc., applied last.

The reason `defaults` lives inside the `.evals.yaml` (and not in a separate global `evals/eval-suite.yaml`) is that defaults are most useful when they're *agent-scoped*. The `code-scout` suite probably wants `judge.model: gpt-5-codex`; the `critic` suite probably wants something else. Per-suite defaults capture that without a single global config trying to be everything.

### Worked example

```yaml
# agents/code-scout.evals.yaml
version: "1"

defaults:
  trials: 5
  required-pass-rate: 0.8
  budget:
    per-trial:
      max-output-tokens: 50000
      max-input-tokens: 200000
      max-tool-calls: 30
      max-turns: 10
      max-wall-seconds: 120
      max-cost-usd: 0.50
  grading:
    citation: { require-all-listed: true }
    lexical:  { case-sensitive: false, match-mode: word }
  judge:
    model: claude-sonnet-4-6                  # judge ≠ subject (code-scout uses gpt-5-codex)
    pass-at: 4

tasks:
  - id: find-grain-implementation
    description: Locate the implementation of ArcherAgentGrain and its core methods.
    tags: [citation, fundamentals]

    prompt: |
      Where is the ArcherAgentGrain class implemented? Give the exact file path
      and briefly describe the methods that handle InitializeAsync,
      AddUserMessageAsync, and CommitFinalAnswerIfStillActiveAsync.

    fixture: archer-2026-04-27

    agent-matrix:
      - id: code-scout
        note: production default (gpt-5-codex + effort=high)
      - id: code-scout-cheap
        note: cost-conscious (gpt-5-mini + effort=low)
        cost-tier: cheap

    expected:
      must-cite-files:
        - src/Archer.Actors/Grains/ArcherAgentGrain.cs
      must-not-cite-files:
        - src/Archer.Actors/Grains/TurnWorkerGrain.cs        # adjacent file
        - src/Archer.Application/Agents/IAgentDefinitionRegistry.cs
      must-mention:
        - InitializeAsync
        - AddUserMessageAsync
        - CommitFinalAnswerIfStillActiveAsync
        - AgentState                                          # state model
      must-call-tool-on-files:
        - tool: list_files
          file-glob: "src/Archer.Actors/**"
        - tool: grep
          pattern-contains: "ArcherAgentGrain"

    budget:
      per-trial:
        max-output-tokens: 30000
        max-tool-calls: 20
        max-cost-usd: 0.20

    judge:
      rubric: |
        The agent was asked to locate ArcherAgentGrain and describe three of its
        methods. Score 0-5 considering:
        - Accuracy: does the cited file path actually contain the class?
        - Method coverage: are all three methods (InitializeAsync,
          AddUserMessageAsync, CommitFinalAnswerIfStillActiveAsync) discussed correctly?
        - Precision: any hallucinated APIs or files?
        Pass if final score >= 4. Output JSON only:
        {"score": <int 0-5>, "verdict": "pass"|"fail", "reasoning": "<one paragraph>"}

    robustness:
      - id: interrupt-mid-turn
        type: interrupt-at-iteration
        iteration: 2
        grader-overrides:
          must-supersede-active-turn: true
```

This single suite runs 2 agents × 2 variants (baseline + interrupt) × 5 trials = 20 trials. Total estimated cost (budget × trials = $0.20 × 20) ≤ $4.00; in practice well under, since most trials don't exhaust the budget.

The same `agents/code-scout.evals.yaml` would typically contain 5–15 more tasks alongside `find-grain-implementation` — `trace-tool-call-flow`, `find-test-coverage-for-X`, `explain-fenced-turn-mechanism`, etc. Each task is its own entry under `tasks:`.

### Cross-references

- [01-architecture](./01-architecture.md) — runner, graders, fixture mounting
- [03-grading](./03-grading.md) — concrete graders + LLM-as-judge
- [04-roadmap](./04-roadmap.md) — bootstrap path, when to add inheritance
