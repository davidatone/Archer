# Agent eval framework — proposal

**Status:** proposal · **Date:** 2026-04-27 · **Triggered by:** an external review noting *"infrastructure tests do not prove agent quality."*

## What this is, in plain English

Right now we have lots of unit tests that prove "the code compiles and the plumbing works." None of them answer the real question: **is the agent actually any good at its job?** This proposal builds a small, repeatable system that asks the agent real questions, looks at its answers, and gives a pass/fail score. Like a school exam, but for the AI.

A Python prototype at [`evals/`](../../../evals/) runs end-to-end today (one task, one judge call, PASS/FAIL output) and was useful for stress-testing the schema. The decision to build slice 1 in C# is independent of it — Archer is a .NET shop and a Python script is a foreign tech stack we'd carry forever. Slice 1 (described below) deletes the prototype and replaces it with a proper C# implementation living alongside the rest of Archer.

## Why we need this

You currently have hundreds of tests covering plumbing — does the data save, does the file write, does the system reject stale work. **Zero tests cover "did the agent answer the question correctly".** Every change to a prompt, a tool, or a model setting *might* make the agent worse, and the only way you'd find out today is when you (or a user) noticed something off.

The reviewer who triggered this proposal said it directly:

> The architecture thinking is 8/10 and the product proof is 4/10. The riskiest assumption is that once the orchestration substrate is elegant, useful multi-agent work will follow. It usually does not. The next best move would be to stop adding framework surface and create a brutal eval suite.

Evals are how you prove the agent is good — and how you catch it the moment a change makes it worse.

## How an eval works, step by step

1. **Write a question** in a small YAML file: which agent to ask, what the prompt is, what a good answer must include.
2. **Run the agent.** Same way you'd run it yourself — same code path, same model, same tools.
3. **Run cheap automatic checks** on the answer:
   - did it mention the right file?
   - did it avoid mentioning a wrong-but-plausible file (the "did the agent invent something" check)?
   - does it contain the key words you'd expect a correct answer to use?
   - did it stay under the budget (tokens, time, tool calls)?
4. **Ask a second AI to grade the answer.** This is the **judge**. It reads the question, the answer, and a rubric you wrote, and says "yes, that's correct" or "no, it's wrong" — with reasoning. This is the only check that catches "all the right keywords but the meaning is wrong."
5. **Verdict:** the trial passes only if the cheap checks *and* the judge both pass.
6. **Run it 5 times, not just once.** AIs are random — sometimes right, sometimes wrong. We need a pass *rate* (e.g., 5 out of 5), not a single result.

Slice 0 already does steps 1–6 for one question.

## Why the second AI ("the judge") is required, not optional

The cheap checks grade the *form* of the answer ("did it use the right words?"). They can't grade the *substance* ("is what it said actually correct?"). An AI can produce all the right keywords and still be wrong — that's the most common failure mode of a model trained to sound confident.

The judge is the only check that grades meaning. Without it, the eval is the reviewer's nightmare ("a beautiful machine for producing mediocre artifacts that pass surface tests"). With it, you have a real verdict.

The cost of the judge is roughly the same as one extra agent turn — significant, but the *whole point*.

A few rules that keep the judge honest:

- **The judge has to be a different AI from the one being tested.** A judge using the same brain as the student grades its own dialect of failure. We enforce this in code; a task that violates it refuses to start.
- **The judge has to give a structured answer**, not free text — `{"score": 0.95, "verdict": "pass", "reasoning": "..."}`. We parse the JSON; we don't read tea leaves.
- **The judge runs at "creativity zero"** so it doesn't drift from run to run.
- **The judge's reasoning is logged** for every verdict, so a wrong call is reviewable later.
- **The judge's cost is reported separately** from the agent's cost, so a runaway judge is visible.

## Why we test multiple versions of the same agent

The same agent can run in different ways:

- **Smart and slow** (top-tier model, deep reasoning) — best answers, expensive.
- **Fast and cheap** (smaller model, light reasoning) — cheaper, but does it still work?
- **No-reasoning** (smaller model, no thinking step) — cheapest, probably worse.

The proposal runs every question against every variant and prints a table:

```
                 smart agent       cheap agent        no-reasoning
Q1 (find grain)  5/5 ✓ $0.18       5/5 ✓ $0.04        3/5 ✗ $0.02
Q2 (trace flow)  5/5 ✓ $0.21       4/5 ✓ $0.06        1/5 ✗ $0.02
```

That table tells you:

- For Q1 the cheap one works fine — run it cheap.
- For Q2 the cheap one is borderline (4/5). Pay up if it matters.
- No-reasoning fails badly. Reasoning is doing real work, not theatre.

You can't read this off the agent's config file. You have to measure it.

## What "robustness" tests mean

Three nasty real-world things every agent has to survive:

- **Interrupt** — the user changes their mind mid-sentence. Does the agent throw away the old work cleanly?
- **Crash** — the program dies in the middle of work. Does it pick up where it left off when it restarts?
- **Missing file** — a file disappears while the agent is reading it. Does it gracefully say "I can't find that," or does it make stuff up?

These tests prove the architecture's "durable, interruptible actor" claims actually hold up — not just on paper.

## Where the YAML files live

Eval suites live **next to the agents they test**, not in a separate tree:

```
agents/
├── code-scout.yaml            ← the agent
├── code-scout.evals.yaml      ← all the eval tasks for code-scout (one suite, many tasks)
├── critic.yaml
├── critic.evals.yaml
└── …

evals/
├── fixtures/repos/            ← frozen-repo snapshots (the "test data")
└── results/                   ← gitignored run output
```

This mirrors how you'd organise unit tests next to the classes they test. The runner globs `agents/*.evals.yaml` and loads every suite. Each suite file holds many tasks plus shared defaults at the top.

## What the working prototype does (today, scheduled for deletion)

`evals/run.py` is a Python prototype that runs end-to-end against the existing CLI. It:

- Reads the question from YAML (in the older centralised `evals/tasks/` layout)
- Spawns the agent (same `archer new` you'd run yourself)
- Reads the agent's answer and tool-call log
- Runs the four cheap checks
- Calls the judge via Azure OpenAI
- Prints PASS / FAIL per trial and an overall verdict
- Exits 0 or 1 so CI could use it

It's a prototype — single task, no multi-agent matrix, no frozen fixtures, no JSON output, runs against the live working copy, and uses the *pre-colocation* layout (`evals/tasks/*.yaml` rather than `agents/<id>.evals.yaml`). It was useful for stress-testing the schema and the judge call. **Slice 1 replaces it with a real `archer eval run` C# command** that uses the colocated layout from the start. The Python script and `evals/tasks/` are both deleted when slice 1 lands.

## The roadmap (intentionally short)

Five slices. The first one shipped without writing any C# at all.

| Slice | Ships | Effort | Why |
|-------|-------|--------|-----|
| ~~0~~ | ~~Python prototype~~ — superseded; the prototype proved the concept, slice 1 is now the first real deliverable | (Python at `evals/run.py` exists) | We considered keeping a throwaway prototype slice; decided it offered no real cost saving over building slice 1 in .NET directly. |
| **1** | A proper `archer eval run` command in C# + colocated `agents/*.evals.yaml` suites + multi-agent table + frozen repo snapshots + JSON output | ~1.5–2 weeks | The first real piece of framework. Fixture pipeline blocks grader work, so it can't fully parallelise. |
| **2** | Robustness tests (interrupt / crash / missing file) | ~1 week | Turn the architecture's claims into measurements. |
| **3** | GitHub CI integration — PRs that make the agent worse fail the build | ~3 days | A mechanical gate. |
| **4** | More questions. **No more code.** | ongoing | Reaching the reviewer's "50 tasks" target is people-time, not engineering. |

The reviewer's main insight applies to the eval itself too: *don't build a framework before proving the simple version works*. The Python prototype (`evals/run.py`) plays that role — it already runs end-to-end and gave us enough confidence to commit to slice 1.

## Decisions to accept or reject

The big choices, with the recommendation:

1. **Three moving parts: a question file, a runner, a grader. Nothing else.** Accept.
2. **Test multiple agent variants side-by-side as a core feature.** Accept (in slice 1).
3. **Use separate config files per variant; don't invent an inheritance system yet.** Accept.
4. **Always run a question 5+ times. Never trust a single run.** Accept (strong).
5. **The judge AI is required for every question — not optional.** Accept (strong).
6. **The judge must be from a different *family* than the agent**, not just a different model name. Accept (strong) — same-family pairings produce correlated false passes, which is the failure mode the judge is supposed to catch. Practical recommendation: GPT-family agents judged by Claude-family models, or vice versa.
7. **Robustness tests are first-class in slice 2.** Accept (strong) — it's how the architecture's claims become measurable.
8. **Eval suites live next to the agent they test (`agents/code-scout.evals.yaml`).** Accept — same ergonomics as a unit test sitting next to its class. Strict-subset rule: a single-agent suite's `agent-matrix` may only list variants of that agent.
9. **Genuinely cross-agent tasks live in `agents/cross-agent.evals.yaml` with a per-cell judge map.** Accept — keeps the simple case simple, makes the cross-agent case explicit.
10. **Skip the throwaway prototype slice; build slice 1 directly in C#.** Accept — keeps everything in one tech stack.

## Where the rest of the documentation lives

```
docs/proposals/eval-framework/
├── README.md              ← you are here. Read this first.
├── 01-architecture.md     reference — how the runner is built
├── 02-task-schema.md      reference — full YAML format + a worked example
├── 03-grading.md          reference — each check in detail, judge rules, aggregation
└── 04-roadmap.md          reference — the five slices, risks, "definition of done"
```

Docs 01–04 are deeper than most readers need. **New here?** Read this README, then run `evals/run.py` against the example question. The reference docs answer specific questions when they come up.

## Related proposal

[`docs/proposals/multi-agent-sdlc/`](../multi-agent-sdlc/README.md) — a much bigger proposal about wiring multiple agents together to do software development. **It explicitly depends on this one.** Multi-agent workflows with no eval suite is the reviewer's exact nightmare. Slice 0 of *this* proposal needs to produce useful signal before that other proposal is worth starting.
