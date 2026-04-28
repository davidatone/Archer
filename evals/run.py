#!/usr/bin/env python3
"""
Slice-0 eval runner for Archer agents.

Reads one task YAML, runs the agent N trials, applies deterministic guardrails
(citation, anti-citation, lexical, budget) plus an LLM judge with a rubric, and
prints a pass/fail line per trial plus a cell summary.

Intentional limits in slice 0:
- single-task (pass the path on the command line)
- single-agent per task (no matrix yet)
- live repo (no frozen fixture)
- no robustness variants (no interrupt-mid-turn etc.)

What this proves: the eval *idea* works against the existing CLI.
If this catches one real regression, slice 1 (a proper C# runner with the
matrix dimension) is worth building. If it doesn't, it's not.

Requirements:
- python 3.11+
- PyYAML  (`python3 -m pip install pyyaml`)
- AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY env vars (for the judge call)
- `dotnet` on PATH; the Archer.Cli built (`dotnet build Archer.slnx` once)

Exit code: 0 if the cell passed (pass-rate >= required), 1 otherwise.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

try:
    import yaml
except ImportError:
    sys.stderr.write("PyYAML not found. Install with: python3 -m pip install pyyaml\n")
    sys.exit(2)

REPO_ROOT = Path(__file__).resolve().parent.parent

# ---------- task model ----------------------------------------------------- #


@dataclass
class TaskSpec:
    id: str
    description: str
    agent: str
    repo: Path
    prompt: str
    must_cite: list[str]
    must_not_cite: list[str]
    must_mention: list[str]
    judge_model: str
    judge_rubric: str
    trials: int
    required_pass_rate: float
    max_output_tokens: int
    max_tool_calls: int
    max_wall_seconds: int

    @classmethod
    def load(cls, path: Path) -> "TaskSpec":
        data = yaml.safe_load(path.read_text())
        expected = data.get("expected", {}) or {}
        judge = data.get("judge", {}) or {}
        budget = data.get("budget", {}) or {}
        return cls(
            id=data["id"],
            description=data.get("description", ""),
            agent=data["agent"],
            repo=(path.parent.parent.parent / data["repo"]).resolve()
            if not Path(data["repo"]).is_absolute()
            else Path(data["repo"]),
            prompt=data["prompt"],
            must_cite=expected.get("must-cite-files", []) or [],
            must_not_cite=expected.get("must-not-cite-files", []) or [],
            must_mention=expected.get("must-mention", []) or [],
            judge_model=judge.get("model", "gpt-5-codex"),
            judge_rubric=judge.get("rubric", ""),
            trials=int(data.get("trials", 3)),
            required_pass_rate=float(data.get("required-pass-rate", 0.66)),
            max_output_tokens=int(budget.get("max-output-tokens", 30000)),
            max_tool_calls=int(budget.get("max-tool-calls", 20)),
            max_wall_seconds=int(budget.get("max-wall-seconds", 180)),
        )


# ---------- agent run ------------------------------------------------------ #


@dataclass
class TrialArtifacts:
    final_answer: str
    tool_calls: list[dict]
    output_tokens: int
    wall_seconds: float
    state_dir: Path
    agent_id: str
    timed_out: bool


def run_agent(task: TaskSpec, state_dir: Path) -> TrialArtifacts:
    """Spawn `archer new` and capture the run's artefacts."""
    cli = REPO_ROOT / "src" / "Archer.Cli"
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(cli),
        "--no-build",
        "--",
        "new",
        "--agent",
        task.agent,
        "--prompt",
        task.prompt,
        "--repo",
        str(task.repo),
        "--state-dir",
        str(state_dir),
    ]
    started = time.monotonic()
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=task.max_wall_seconds,
            cwd=REPO_ROOT,
        )
        timed_out = False
    except subprocess.TimeoutExpired:
        timed_out = True
        proc = None
    wall = time.monotonic() - started

    # The CLI prints "Agent id:   <id>" early; lift it out of stdout.
    agent_id = ""
    if proc is not None:
        for line in proc.stdout.splitlines():
            if line.startswith("Agent id:"):
                agent_id = line.split(":", 1)[1].strip()
                break

    final_answer = ""
    tool_calls: list[dict] = []
    output_tokens = 0
    if agent_id:
        events_path = state_dir / "agents" / agent_id / "events.ndjson"
        if events_path.exists():
            for line in events_path.read_text().splitlines():
                if not line.strip():
                    continue
                try:
                    evt = json.loads(line)
                except json.JSONDecodeError:
                    continue
                kind = evt.get("Kind") or evt.get("kind") or ""
                if kind == "final_answer":
                    final_answer = evt.get("Text") or evt.get("text") or ""
                elif kind == "tool_call_started":
                    tool_calls.append(
                        {
                            "tool": evt.get("ToolName") or evt.get("toolName"),
                            "args": evt.get("Arguments") or evt.get("arguments"),
                        }
                    )

    # Best-effort token estimate from the answer length (the runner's CLI
    # doesn't yet emit token counts to stdout). Slice 1 will read OTel metrics.
    output_tokens = len(final_answer) // 4

    return TrialArtifacts(
        final_answer=final_answer,
        tool_calls=tool_calls,
        output_tokens=output_tokens,
        wall_seconds=wall,
        state_dir=state_dir,
        agent_id=agent_id,
        timed_out=timed_out,
    )


# ---------- deterministic graders ------------------------------------------ #

PATH_RE = re.compile(
    r"((?:[a-zA-Z0-9_\-./]+/)+[a-zA-Z0-9_\-.]+\.(?:cs|md|yaml|yml|json|csproj|slnx|props|targets))"
)


def grade_citation(task: TaskSpec, artefacts: TrialArtifacts) -> tuple[bool, str]:
    if not task.must_cite:
        return True, "no must-cite predicates"
    cited = {m.replace("\\", "/") for m in PATH_RE.findall(artefacts.final_answer)}
    missing = [
        expected
        for expected in task.must_cite
        if not any(c.endswith(expected) for c in cited)
    ]
    if missing:
        return False, f"missing: {', '.join(missing)}"
    return True, f"cited all {len(task.must_cite)} expected files"


def grade_anti_citation(task: TaskSpec, artefacts: TrialArtifacts) -> tuple[bool, str]:
    if not task.must_not_cite:
        return True, "no anti-citation predicates"
    cited = {m.replace("\\", "/") for m in PATH_RE.findall(artefacts.final_answer)}
    hallucinated = [
        bad for bad in task.must_not_cite if any(c.endswith(bad) for c in cited)
    ]
    if hallucinated:
        return False, f"hallucinated: {', '.join(hallucinated)}"
    return True, "no forbidden citations"


def grade_lexical(task: TaskSpec, artefacts: TrialArtifacts) -> tuple[bool, str]:
    if not task.must_mention:
        return True, "no must-mention predicates"
    missing = [
        term
        for term in task.must_mention
        if not re.search(rf"\b{re.escape(term)}\b", artefacts.final_answer)
    ]
    if missing:
        return False, f"missing terms: {', '.join(missing)}"
    return True, f"mentioned all {len(task.must_mention)} expected terms"


def grade_budget(task: TaskSpec, artefacts: TrialArtifacts) -> tuple[bool, str]:
    failures = []
    if artefacts.output_tokens > task.max_output_tokens:
        failures.append(f"tokens {artefacts.output_tokens}>{task.max_output_tokens}")
    if len(artefacts.tool_calls) > task.max_tool_calls:
        failures.append(
            f"tool-calls {len(artefacts.tool_calls)}>{task.max_tool_calls}"
        )
    if artefacts.wall_seconds > task.max_wall_seconds or artefacts.timed_out:
        failures.append(
            f"wall {artefacts.wall_seconds:.1f}s>{task.max_wall_seconds}s"
        )
    if failures:
        return False, "; ".join(failures)
    return (
        True,
        f"{artefacts.output_tokens} tok, {len(artefacts.tool_calls)} tool calls, "
        f"{artefacts.wall_seconds:.1f}s",
    )


# ---------- LLM judge ------------------------------------------------------ #


def call_judge(task: TaskSpec, artefacts: TrialArtifacts) -> tuple[bool, dict]:
    """Call Azure OpenAI Responses API with a structured rubric.

    Returns (passed, payload). payload is the parsed JSON the judge produced
    (or an error envelope if the call/parse failed).
    """
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT")
    api_key = os.environ.get("AZURE_OPENAI_API_KEY")
    if not endpoint or not api_key:
        return False, {
            "error": "AZURE_OPENAI_ENDPOINT or AZURE_OPENAI_API_KEY not set",
        }

    judge_prompt = (
        "You are a strict, fair grader. Apply the rubric below to the agent's "
        "answer and return only a single JSON object — no prose, no markdown.\n\n"
        f"--- Agent's question ---\n{task.prompt}\n\n"
        f"--- Agent's answer ---\n{artefacts.final_answer}\n\n"
        f"--- Rubric ---\n{task.judge_rubric}\n"
    )

    url = (
        endpoint.rstrip("/")
        + f"/openai/v1/responses?api-version=2025-04-01-preview"
    )
    body = {
        "model": task.judge_model,
        "input": judge_prompt,
        "max_output_tokens": 1000,
        "temperature": 0,
    }
    req = urllib.request.Request(
        url,
        data=json.dumps(body).encode("utf-8"),
        method="POST",
        headers={
            "Content-Type": "application/json",
            "api-key": api_key,
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            response_body = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return False, {"error": f"judge HTTP {e.code}: {e.read().decode()[:300]}"}
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        return False, {"error": f"judge network failure: {e}"}

    # Pull the assistant text out of the Responses API envelope. Different model
    # families place it slightly differently; try the common shapes.
    text = ""
    for item in response_body.get("output", []):
        for piece in item.get("content", []):
            if piece.get("type") in {"output_text", "text"}:
                text = piece.get("text", "")
                if text:
                    break
        if text:
            break
    if not text:
        text = response_body.get("output_text", "")

    text = text.strip()
    # Strip optional ```json fences.
    if text.startswith("```"):
        text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text, flags=re.MULTILINE).strip()
    try:
        verdict = json.loads(text)
    except json.JSONDecodeError:
        return False, {"error": "judge returned non-JSON", "raw": text[:300]}

    passed = (verdict.get("verdict") or "").lower() == "pass"
    return passed, verdict


# ---------- trial loop ----------------------------------------------------- #


@dataclass
class TrialResult:
    trial: int
    passed: bool
    graders: dict


def run_trial(task: TaskSpec, trial_num: int) -> TrialResult:
    state_dir = Path(tempfile.mkdtemp(prefix=f"archer-eval-{task.id}-t{trial_num}-"))
    try:
        artefacts = run_agent(task, state_dir)
        if artefacts.timed_out:
            return TrialResult(
                trial=trial_num,
                passed=False,
                graders={"budget": (False, "timed out")},
            )

        graders: dict[str, tuple[bool, str]] = {}
        graders["citation"] = grade_citation(task, artefacts)
        graders["anti-citation"] = grade_anti_citation(task, artefacts)
        graders["lexical"] = grade_lexical(task, artefacts)
        graders["budget"] = grade_budget(task, artefacts)

        # Judge runs *regardless* of guardrail outcome — surface text and meaning
        # are independent failure modes, both worth seeing.
        judge_passed, judge_payload = call_judge(task, artefacts)
        graders["judge"] = (
            judge_passed,
            f"score={judge_payload.get('score')} verdict={judge_payload.get('verdict')} — "
            f"{(judge_payload.get('reasoning') or judge_payload.get('error') or '')[:140]}",
        )

        # Cell verdict is conjunctive: every grader must pass.
        passed = all(verdict for verdict, _ in graders.values())
        return TrialResult(trial=trial_num, passed=passed, graders=graders)
    finally:
        shutil.rmtree(state_dir, ignore_errors=True)


# ---------- main ----------------------------------------------------------- #


def render_trial(t: TrialResult) -> str:
    icon = "✓" if t.passed else "✗"
    lines = [f"  trial {t.trial}: {icon} {'PASS' if t.passed else 'FAIL'}"]
    for grader, (ok, summary) in t.graders.items():
        gicon = "✓" if ok else "✗"
        lines.append(f"    {gicon} {grader:13} {summary}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Archer eval runner (slice-0)")
    parser.add_argument("task", type=Path, help="path to task YAML")
    parser.add_argument(
        "--build",
        action="store_true",
        help="run `dotnet build` once before trials (otherwise --no-build assumes it's built)",
    )
    args = parser.parse_args()

    task = TaskSpec.load(args.task)

    if args.build:
        print("Building Archer.slnx …")
        subprocess.check_call(
            ["dotnet", "build", "Archer.slnx", "--nologo"], cwd=REPO_ROOT
        )

    print(f"Task:        {task.id}")
    print(f"Description: {task.description}")
    print(f"Agent:       {task.agent}   Repo: {task.repo}")
    print(f"Trials:      {task.trials}, required pass-rate: {task.required_pass_rate}")
    print()

    results = [run_trial(task, i + 1) for i in range(task.trials)]
    for r in results:
        print(render_trial(r))
        print()

    passed = sum(1 for r in results if r.passed)
    rate = passed / len(results)
    cell_passed = rate >= task.required_pass_rate

    print("─" * 72)
    print(
        f"Cell verdict: {'PASS' if cell_passed else 'FAIL'} "
        f"({passed}/{len(results)} = {rate:.0%}; required {task.required_pass_rate:.0%})"
    )
    return 0 if cell_passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
