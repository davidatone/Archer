# Archer evals — Python prototype

> **Status: prototype, scheduled for deletion.** This Python script proved the eval *shape* end-to-end (one task, guardrails + judge, PASS/FAIL output). Slice 1 in [`docs/proposals/eval-framework/04-roadmap.md`](../docs/proposals/eval-framework/04-roadmap.md) replaces it with a proper `archer eval run` C# command. When slice 1 ships, this directory's `run.py`, `tasks/`, and most of this README are deleted; only `fixtures/` and `results/` survive. The colocation layout (`agents/<id>.evals.yaml` next to the agent it tests) is slice 1's; this prototype uses the older centralised `evals/tasks/` layout because that's how it was built. **Don't take this layout as canonical** — it's the prototype's, not the framework's.

Smallest possible eval harness for Archer agents. One Python script, one task
YAML, one judge call per trial. The point of this prototype was to **prove the eval
idea works against the existing CLI** before committing to C# framework code.

If this catches one real regression, the C# slice 1 in
[`docs/proposals/eval-framework/04-roadmap.md`](../docs/proposals/eval-framework/04-roadmap.md)
is worth building. If it doesn't catch anything useful, the proposal is wrong
and we should rethink before committing more time.

## What an eval is, here

```
┌───────────────────┐     ┌───────────────────┐     ┌───────────────────┐
│   task YAML       │     │   run.py          │     │   per-trial       │
│   (prompt +       │ ──> │   spawns agent    │ ──> │   guardrails      │
│    expected +     │     │   N times         │     │     +             │
│    judge rubric)  │     │   captures answer │     │   LLM judge       │
└───────────────────┘     └───────────────────┘     └───────────────────┘
                                                              │
                                                              ▼
                                                       PASS / FAIL  +
                                                       cell pass-rate
```

Each trial passes through:

1. **Run the agent** via `dotnet run --project src/Archer.Cli -- new` against
   the task's repo + prompt.
2. **Read the answer** from the agent's `events.ndjson` (the `final_answer` event).
3. **Apply guardrails:**
   - `citation` — must-cite-files appear in the answer
   - `anti-citation` — must-not-cite-files do NOT appear (hallucination guard)
   - `lexical` — must-mention terms appear as whole words
   - `budget` — tokens / tool calls / wall time within caps
4. **Call the judge** — Azure OpenAI Responses API with a rubric. Returns
   `{score, verdict, reasoning}`.
5. **Combined verdict** — trial passes only if every grader passes (including
   the judge). Cell passes if `passed-trials / total-trials >= required-pass-rate`.

The judge is **not optional**. Guardrails catch cheap failures (wrong file,
missing word); the judge catches "all the right words but the answer is still
wrong" — which is the failure mode an LLM-driven agent is most likely to hit.

## Requirements

- `python3` 3.11+ on PATH
- `pip install pyyaml`
- `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_API_KEY` env vars (for the judge call)
- `dotnet` 10.x; `dotnet build Archer.slnx` run once before the first eval

The judge model defaults to `gpt-5-codex` and is configurable per task. It
**must differ from the agent's model** — a judge using the same model as the
subject grades its own dialect of failure.

## Running

```bash
# One-time: build the CLI.
dotnet build Archer.slnx

# Set the env vars (or `source` them from a dotenv file).
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com"
export AZURE_OPENAI_API_KEY="…"

# Run the slice-0 task.
./evals/run.py evals/tasks/001-find-grain-implementation.yaml
```

Add `--build` to rebuild before running:

```bash
./evals/run.py evals/tasks/001-find-grain-implementation.yaml --build
```

Output:

```
Task:        001-find-grain-implementation
Description: Locate the implementation of ArcherAgentGrain and summarise its core methods.
Agent:       code-scout   Repo: /Users/davidturner/dev/actor-model
Trials:      3, required pass-rate: 0.66

  trial 1: ✓ PASS
    ✓ citation      cited all 1 expected files
    ✓ anti-citation no forbidden citations
    ✓ lexical       mentioned all 3 expected terms
    ✓ budget        4823 tok, 6 tool calls, 47.2s
    ✓ judge         score=5 verdict=pass — Cited correct path …

  trial 2: ✗ FAIL
    ✓ citation      cited all 1 expected files
    ✓ anti-citation no forbidden citations
    ✗ lexical       missing terms: CommitFinalAnswerIfStillActiveAsync
    ✓ budget        3920 tok, 5 tool calls, 38.1s
    ✗ judge         score=3 verdict=fail — Discusses two of three methods …

  trial 3: ✓ PASS
    ✓ citation      cited all 1 expected files
    ✓ anti-citation no forbidden citations
    ✓ lexical       mentioned all 3 expected terms
    ✓ budget        5102 tok, 7 tool calls, 51.6s
    ✓ judge         score=4 verdict=pass — Correct file and methods …

────────────────────────────────────────────────────────────────────────
Cell verdict: PASS (2/3 = 67%; required 66%)
```

Exit code is 0 on cell pass, 1 on cell fail — wire it into a `Makefile` or
GitHub Action however you want.

## Adding tasks (prototype layout — superseded by slice 1)

Drop another YAML in `evals/tasks/`, copying the format from
[`001-find-grain-implementation.yaml`](./tasks/001-find-grain-implementation.yaml).

> **Slice 1 will move all task YAMLs out of `evals/tasks/` and into colocated suites at `agents/<id>.evals.yaml`** (one suite per agent, multiple tasks inside, suite-level defaults). If you're authoring a task you intend to keep long-term, write it as a draft of the colocation format ([proposal § Worked example](../docs/proposals/eval-framework/02-task-schema.md#worked-example)) and port it over when slice 1 lands. The prototype's per-task layout below is *only* for prototype iteration.

The minimum fields:

| Field | What it does |
|-------|--------------|
| `id` | Unique within the directory. |
| `agent` | Agent definition id (must exist in `agents/`). |
| `repo` | Path to the repo to investigate (relative to repo root in slice 0). |
| `prompt` | The first user prompt. |
| `expected.must-cite-files` | At least one file path that should appear in the answer. |
| `expected.must-not-cite-files` | Plausible-but-wrong paths that should NOT appear. |
| `expected.must-mention` | Key terms that should appear (whole-word match). |
| `judge.rubric` | The rubric the judge applies. End with the JSON output spec. |
| `trials`, `required-pass-rate` | Multi-trial knobs (defaults 3 and 0.66). |
| `budget.*` | Tokens / tool-calls / wall-seconds caps per trial. |

## What slice 0 deliberately doesn't do

| Feature | Where it lands |
|---------|----------------|
| Multi-agent matrix (compare `code-scout` vs `code-scout-cheap`) | Slice 1 |
| Frozen-fixture tarballs with sha256 verification | Slice 1 |
| OTel-based token/cost accounting | Slice 1 |
| Multiple tasks per run + result table | Slice 1 |
| Robustness variants (interrupt-mid-turn, kill-host-mid-trial, delete-file) | Slice 2 |
| CI integration with regression baseline | Slice 3 |
| JSON result file + diff against baseline | Slice 1 |

Slice 0 is intentionally crude. It exists to validate the *shape* of the
solution before committing to a C# project structure. If after running 5 tasks
this script feels right, slice 1 inherits the design with a richer runner.

## Cost

Each trial = one agent run + one judge call. For task 001:

- agent run: ~5k input + ~5k output tokens against `code-scout` (gpt-5-codex)
- judge call: ~4k input + ~300 output tokens against `gpt-5-codex` as judge

Rough back-of-envelope at current pricing: **$0.05–$0.15 per trial**. Three
trials = under $0.50 per task per run.

If you spam the script while iterating, set `trials: 1` in the YAML to dampen
the cost while developing the predicate set.

## When the cell fails

Failure modes and what they tell you:

| What's failing | Likely cause | First thing to check |
|----------------|--------------|----------------------|
| `citation` always fails | Agent isn't grounding its answer in the file | Check the events log for `tool_call_started` entries — is it actually opening the file? |
| `anti-citation` fails | Agent is hallucinating an adjacent file | Tighten the prompt; consider lowering reasoning effort *up* (paradoxically, hallucinations sometimes increase as effort drops) |
| `lexical` fails | Answer is roughly right but missing key terms | The judge will probably also fail; if the judge passes, your `must-mention` list is too strict |
| `budget` fails | Agent took too long or used too many tools | Either the task is too open-ended, or you set the budget too tight |
| `judge` fails despite guardrails passing | Surface text is right, substance is wrong — exactly the case the judge exists to catch | Read the judge's `reasoning` field; it usually quotes the specific defect |

## Cross-references

- [Proposal — README](../docs/proposals/eval-framework/README.md)
- [Proposal — task schema](../docs/proposals/eval-framework/02-task-schema.md)
- [Proposal — grading](../docs/proposals/eval-framework/03-grading.md)
- [Proposal — roadmap](../docs/proposals/eval-framework/04-roadmap.md)
