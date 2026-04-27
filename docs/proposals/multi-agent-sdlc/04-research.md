## Research — ChatDev, AutoGen, LangGraph, CrewAI, Magentic-One

**Status:** proposal · **See also:** [01-architecture](./01-architecture.md), [05-roadmap](./05-roadmap.md)

This document captures what we learned from existing multi-agent orchestration systems — primarily ChatDev (the user-cited reference) and the four other systems most often compared with it. The goal is to ground our design choices in *what's actually been tried*, distinguish lessons-to-adopt from anti-patterns-to-avoid, and produce a calibrated comparison so reviewers can sanity-check that we're not reinventing the worst version of someone else's wheel.

### TL;DR — five takeaways that shape this proposal

1. **ChatDev classic is two-agent only, and the same team abandoned that for a graph runtime in 2.0.** Don't ship a linear-chain-only system; design for DAG from day one. We do.
2. **A blackboard is the right inter-phase contract.** Direct agent-to-agent messaging across phases creates ad-hoc dependencies and untestable orchestration. Phases read inputs at start, write artifacts at end; everything else flows through the typed shared store. We model this as the workflow's artifact set + workspace.
3. **Phases as first-class units beats agents-as-orchestrators.** ChatDev's `Phase` class encapsulates *(state-pull, dialogue, state-push)*; LangGraph's nodes do the same; CrewAI's tasks do the same. We follow.
4. **Two-tier config: structure in code, parameters in declarative files.** Phases are typed (a `mode` enum + named handlers); roles, prompts, rubrics, peers, max-rounds live in YAML. Resist a purely declarative phase model — you'll re-invent business logic in YAML and regret it.
5. **Termination is a multi-signal predicate, not a magic string.** ChatDev's `<INFO>` token is fragile; modern alternatives layer structured tool calls (sign_off), max-rounds budgets, and external truth (compiler/test exit codes). We compose all three.

### ChatDev classic — the canonical reference

Repo: github.com/OpenBMB/ChatDev (branch `chatdev1.0`). The system simulates a software company's SDLC.

#### Architecture

Three top-level packages drive everything:

| Package | Role |
|---------|------|
| `camel/` | Generic two-agent role-play primitive (forked from CAMEL-AI) — `ChatAgent`, `RolePlaying`, message types |
| `chatdev/` | The actual SDLC orchestrator — `ChatChain`, `Phase`, `ComposedPhase`, `ChatEnv` |
| `CompanyConfig/` | JSON-defined "companies": Default, Art, Human, Incremental |

Entry point `run.py` is a 6-line ceremony:
```
chat_chain = ChatChain(...)
chat_chain.pre_processing()
chat_chain.make_recruitment()
chat_chain.execute_chain()
chat_chain.post_processing()
```

Key files (line counts to give a sense of scale):
- `chatdev/chat_chain.py` — 365 lines, top-level orchestrator
- `chatdev/phase.py` — 652 lines, abstract `Phase` + 14 concrete phases
- `chatdev/composed_phase.py` — 252 lines, looped/conditional phase wrappers
- `chatdev/chat_env.py` — 310 lines, the global blackboard
- `chatdev/roster.py` — 20 lines, a flat list of role names (the "agent registry")
- `camel/agents/role_playing.py` — 279 lines, two-agent dialogue primitive
- `camel/agents/chat_agent.py` — 292 lines, single agent with message history

#### The ChatChain mechanic

For one `SimplePhase`, the loop is:

1. **Phase init** — `chat_chain.py:97-110`: dynamic import → instantiate the phase class named after the JSON key.
2. **Pull state** — `update_phase_env(chat_env)`: copy needed keys from `ChatEnv.env_dict` into a local placeholder dict for prompt formatting.
3. **Construct RolePlaying session** — `phase.py:97-109`: assistant + user agents, both seeded with role-specific system prompts.
4. **Init chat** — `RolePlaying.init_chat`: the user agent's prompt is the phase instruction, formatted; the assistant's first input is what the user agent says.
5. **Turn loop** — `phase.py:125-163`: up to `chat_turn_limit` iterations. Each iteration is **two LLM calls** (assistant *then* user). Either side can terminate by emitting `<INFO> result`.
6. **Reflection** (optional) — if no `<INFO>` was emitted, a *separate* RolePlaying session between **CEO and Counselor** reads the transcript and emits a single-line conclusion. CEO+Counselor is hardcoded — not configurable.
7. **Push state** — `update_chat_env`: write the conclusion (or extracted code) back to `ChatEnv.codes`, `ChatEnv.requirements`, etc.

`ComposedPhase` (`composed_phase.py:119-162`) wraps a list of SimplePhases in `for cycle in range(1, cycle_num+1)` with a `break_cycle(phase_env)` predicate. Used by `CodeReview` (3 cycles) and `Test` (loop until no bugs).

**Critical design property:** every chat is exactly two agents. There is no group chat, no N-participant room. "Multi-agent" emerges from the *sequence* of two-agent dialogues, not from any room with N participants. Nine recruited "employees" never actually meet — they're paired up by phase.

#### Configuration

Three JSON files per "company":

`ChatChainConfig.json` — linear pipeline + global flags:
```json
{
  "chain": [
    {"phase":"DemandAnalysis","phaseType":"SimplePhase","max_turn_step":-1,"need_reflect":"True"},
    {"phase":"CodeReview","phaseType":"ComposedPhase","cycleNum":3,
     "Composition":[
       {"phase":"CodeReviewComment","phaseType":"SimplePhase","max_turn_step":1,"need_reflect":"False"},
       {"phase":"CodeReviewModification","phaseType":"SimplePhase","max_turn_step":1,"need_reflect":"False"}
     ]}
  ],
  "recruitments": ["Chief Executive Officer", "Programmer", ...]
}
```

`PhaseConfig.json` — per-phase prompt + role pairing:
```json
"DemandAnalysis": {
  "assistant_role_name":"Chief Product Officer",
  "user_role_name":"Chief Executive Officer",
  "phase_prompt":[
    "...",
    "Once we all agree, ... terminate the discussion by replying with only one line, which starts with a single word <INFO>"
  ]
}
```

`RoleConfig.json` — flat dict of role name → list of prompt lines.

**Tunable in JSON:** chain order, cycle counts, turn limits, reflect flag, role-to-phase pairing, prompt bodies, recruited roster, global flags.
**Hardcoded in Python:** phase class existence, what state each phase reads/writes, termination predicates of ComposedPhases, the `<INFO>` token convention, the reflection role pair.

This hybrid is *deliberate*: structure in code (because each phase has unique state semantics) and parameters in JSON (because role names, prompts, and turn counts shouldn't require a recompile).

#### Memory

Two distinct mechanisms:

**Per-agent dialogue memory.** Each `ChatAgent` has `stored_messages: List[MessageType]`. Reset between phases — `RolePlaying.init_chat` calls `assistant.reset()` and `user.reset()`. Agents do **not** remember earlier phases.

**Shared blackboard = `ChatEnv`.** This is how state crosses phase boundaries:
- `env_dict` — string fields like `task_prompt`, `modality`, `language`, `review_comments`, `error_summary`, `test_reports`
- `codes: Codes` — code artifact (parsed markdown blocks, written to `WareHouse/<project>_<timestamp>/`)
- `requirements: Documents`, `manuals: Documents`
- `proposed_images`, `incorporated_images` — for the Art phase

Optional vector memory (`ecl/memory.py`) exists but is gated by `with_memory: "True"` and only activates for Code Reviewer / Programmer / Test Engineer roles.

#### Reviewer/critic pattern

Two flavours, both unstructured:

1. **Reviewer-as-phase** — `CodeReview` ComposedPhase alternates CodeReviewComment (Code Reviewer ↔ Programmer) and CodeReviewModification (Programmer ↔ CTO), up to 3 cycles. Comments are free-form text; the loop breaks on `<INFO> Finished`. No rubric, no JSON schema, no severity levels.
2. **Reflection** — when `need_reflect:True` and no `<INFO>` was emitted, CEO+Counselor re-read the whole transcript and emit a one-line conclusion. Hardcoded role pair.

Bug feedback in `Test` is more structured because the truth is external: `chat_env.exist_bugs()` literally runs `python main.py` and captures stderr; that text becomes the `test_reports` placeholder fed into `TestErrorSummary`'s prompt. The "tester agent" is really `subprocess + LLM-summariser`.

This last bit is **the most important pattern in ChatDev classic to adopt:** when an external system can decide truth (compiler, test runner, type checker), use it. Don't ask an LLM to imagine test results.

### ChatDev 2.0 ("DevAll") — what they moved to

The `main` branch is now a generic DAG/graph runtime. Key files:
- `runtime/node/registry.py`, `runtime/edge/processors`, `runtime/edge/conditions`
- `workflow/graph.py`, `workflow/graph_manager.py`, `workflow/cycle_manager.py`
- `yaml_instance/` — YAML-defined subgraphs; `ChatDev_v1.yaml` ports the classic flow

Nodes are typed (agent / executor / splitter); edges carry processors and conditions; a cycle manager handles loops.

**This is essentially a LangGraph-style refactor of ChatDev.** The fact that the original team concluded the linear two-agent chain didn't scale, and *moved to a graph runtime with explicit edge conditions*, is the single most useful piece of evidence for our design.

### AutoGen — the GroupChat / SelectorGroupChat pattern

Microsoft's AutoGen (`autogen-core`, `autogen-agentchat`) treats a group chat as a first-class abstraction. Multiple agents share one message log; a `GroupChatManager` decides whose turn is next. Flavours:

- `RoundRobinGroupChat` — cycle through participants in declared order.
- `SelectorGroupChat` — an LLM reads recent messages and selects the next speaker. Configurable model (often a cheaper one than the participants).
- `Swarm` (autogen-agentchat 0.4+) — agents can hand off via tool calls (`HandoffMessage`); the manager honours the handoff.
- `MagenticOneGroupChat` — uses Magentic-One's orchestrator pattern (see below) inside a group chat.

Termination is composable:
```python
termination = (
    MaxMessageTermination(20)
    | TextMentionTermination("APPROVED")
    | HandoffTermination(target="user")
)
```

`|` is logical OR; `&` is AND. This is what we want for our `completion` block: a small composable algebra of predicates rather than a single magic-string match.

Memory: shared message list visible to all group members; agents have own context windows. State is in-process by default; persistence is opt-in via `BaseGroupChat.save_state() / load_state()` returning a JSON-serialisable dict.

**Lesson adopted:** N-agent group chat with a pluggable selector is the right shape for our peer mode. Round-robin → mention-aware → LLM-selector is a sensible incremental ladder.

**Lesson rejected:** AutoGen's heavy reliance on Python decorators for agent definition. Our agents are YAML-defined; the orchestration is C# code. This is structurally similar but with declarative agents, imperative orchestration.

### LangGraph — stateful directed graphs

LangGraph (langchain-ai) treats orchestration as a `StateGraph(TypedDict)`. You declare a state schema (e.g. `class State(TypedDict): messages: list, draft: str`), then add nodes (functions that read state and return state updates) and edges (typed transitions, optionally conditional).

```python
graph = StateGraph(State)
graph.add_node("draft", draft_node)
graph.add_node("review", review_node)
graph.add_node("revise", revise_node)
graph.add_edge("draft", "review")
graph.add_conditional_edges("review",
    decide_next,                       # function returns "revise" or END
    {"revise": "revise", END: END})
graph.add_edge("revise", "review")
graph.set_entry_point("draft")
app = graph.compile(checkpointer=MemorySaver())
```

Key features that matter to us:

1. **State updates use reducers.** Multiple nodes can update the same key; the reducer (`Annotated[list, add]`) defines how to merge. Crucial for peer mode where N agents append to the same transcript.
2. **Checkpointers** persist state at every node boundary so a graph can pause and resume after a host restart. Maps directly onto Orleans grain state persistence.
3. **Subgraphs** — a node can be another compiled graph. Lets you build phase libraries that compose.
4. **Streaming** — the graph can stream intermediate state updates. Maps onto our event sink.

**Lesson adopted:** explicit DAG with typed state and conditional edges. Our v1 ships linear-chain-as-degenerate-DAG; v2 adds branching and joins; v3 adds conditional edges (`if rubric-score < 0.5 then revise else seal`).

**Lesson rejected:** LangGraph's heavy state-schema cognitive load for simple workflows. We hide the DAG behind a YAML schema for the common case; expose the underlying graph model only for advanced workflows.

### CrewAI — sequential and hierarchical

CrewAI (crewai-inc/crewai) frames work as a `Crew` of `Agent`s working through a list of `Task`s. Two execution modes:

- **Sequential** — tasks run in declared order; output of task N feeds task N+1.
- **Hierarchical** — a manager agent (LLM-driven) decides task assignment dynamically.

YAML support:
```yaml
researcher:
  role: Research Specialist
  goal: Uncover cutting-edge developments in AI
  backstory: >
    You're a seasoned researcher with a knack for ...
  tools: [search_tool, scrape_tool]
```

Tasks are similarly YAML-defined with `description`, `expected_output`, `agent`, `context` (prior task references).

**Lesson adopted:** YAML-first agent and workflow definitions. Our schema follows a similar declarative pattern.

**Lesson rejected:** CrewAI conflates "agent" and "team" — an agent definition includes a "backstory" and a single goal, suitable for a one-shot task but awkward when the same agent profile is used across many workflows. We separate role-as-profile (existing `AgentDefinition`) from role-in-this-phase (the phase YAML's framing).

### Magentic-One — orchestrator + specialised assistants

Microsoft Research's Magentic-One (`microsoft/autogen` examples + the paper) uses a single `Orchestrator` LLM that maintains two ledgers:

- **Task ledger** — facts known, facts to find, current plan
- **Progress ledger** — what's done, what's stuck, who's next

Specialised assistants are leaf agents with narrow capabilities: `FileSurfer`, `WebSurfer`, `Coder`, `ComputerTerminal`. The orchestrator delegates to them and integrates their outputs.

The pattern is **hierarchical**, not peer. The orchestrator is the only entity that talks to assistants; assistants don't talk to each other.

**Lesson adopted:** explicit task + progress ledgers (i.e. structured workflow state, not just chat history). Our `WorkflowState` carries phase status, artifact summaries, and outstanding sign-offs — analogous to a progress ledger.

**Lesson rejected:** single-orchestrator architecture has a bottleneck — the orchestrator's context window is the system bottleneck and the orchestrator can become a weak link if it stalls. Our peer mode is genuinely peer-to-peer; the workflow grain handles control flow, not reasoning.

### Comparison table

| Dimension | **ChatDev classic** | **AutoGen** | **LangGraph** | **CrewAI** | **Magentic-One** | **Archer (proposed)** |
|---|---|---|---|---|---|---|
| Orchestration shape | Linear chain of two-agent role-plays, JSON-configured | Group chat with selector | Stateful DAG with conditional edges | Sequential or hierarchical task list | Single orchestrator + leaf assistants | DAG runtime with three phase modes; v1 ships linear; YAML-configured |
| Multi-agent collab mode | 2 agents per phase (sequential of dialogues) | Chat room (N agents, all see all) | Graph nodes; parallel branches; convergence at join nodes | Sequential default, hierarchical = manager-driven | Hierarchical (orchestrator → leaves) | Three modes: solo, critic (1+N), peer (N) |
| Configuration | 3 JSON files + Python phase classes | Mostly Python; YAML for some agents | Python graph builder; TypedDict state | Python decorators + YAML | Python | YAML for workflows + agents + rubrics; C# for phase mode handlers |
| Memory / artifacts | Global `ChatEnv` blackboard; per-agent message reset per phase | Shared message list per group | Explicit `State` w/ reducers; checkpointer for persistence | Shared task context | Orchestrator-maintained ledgers | Workspace + artifact set; per-phase agent reset; durable workflow grain state |
| Convergence / termination | `<INFO>` token, OR turn limit, OR `break_cycle` predicate | Composable predicates (`MaxMessageTermination | TextMentionTermination | …`) | Edges to `END` node + `should_continue` functions | Task list exhaustion or manager decision | Composable predicates in YAML (sign-off, all-critics-pass, max-rounds, OR fallbacks) |
| Critic pattern | CEO+Counselor reflection (hardcoded); reviewer-as-phase | Critic agent in group chat | Critic node added to graph | Reviewer task | Orchestrator self-critiques via ledger | Structured critic mode with rubrics + `comment_on_artifact` tool; configurable critics per phase |
| Branching/loops | Composed phases with `break_cycle` | Group-chat termination + handoff | Conditional edges + cycles | Limited | Replan loop in orchestrator | Linear in v1; DAG with joins in v2; conditionals + cycles in v3 |
| Strength | Simple, deterministic, debuggable | Flexible group dynamics | Composable, parallel, observable | Easy mental model | Strong on heterogeneous tasks | Built for durability (Orleans), declarative auth, and three first-class collab modes |
| Weakness | 2-agent only, no parallelism, hardcoded phase classes | Selector latency; chat can drift | More plumbing; state-design overhead | Limited concurrency primitives | Single-orchestrator bottleneck | Orleans setup overhead; v1 doesn't ship branches |

### Specific patterns adopted from ChatDev

1. **Blackboard as inter-phase contract.** `ChatEnv` → our workspace+artifacts. Phases read at start, write at end. Survives crashes naturally with grain state.
2. **Phases as first-class units.** `Phase.execute()` triple (state-pull, dialogue, state-push) → our phase grain interface.
3. **Two-tier config.** Phase classes mandatory in code; roles/prompts/cycle counts data-driven.
4. **ComposedPhase with break_cycle predicate.** "Loop a sub-pipeline until predicate" → our critic-mode revision loop and peer-mode max-rounds.
5. **Per-phase agent reset.** Fresh context per phase; long-term knowledge in artifacts, not chat.
6. **OS-as-tester (for future Test phases).** Compile/run/capture; LLM summarises rather than imagines.

### Specific patterns *avoided* from ChatDev

1. **Two-agent-only restriction.** Our peer mode has N peers in one room.
2. **Trivial role registry (`Roster` is a `List<string>`).** Our `AgentDefinition` carries tools, model, context profile, capabilities; the workflow uses these to validate phase wiring.
3. **Hardcoded reflection role pair (CEO+Counselor).** Our critics are configurable per phase; same for the deciding peer.
4. **Magic-string termination as the only signal.** We compose structured tools (sign_off, comment_on_artifact), max-rounds budgets, and (future) external truth (test exit codes).
5. **Linear chain only.** Designed for DAG from day 1; v1 happens to be linear.
6. **Two LLM calls per turn (`RolePlaying.step`).** Doubles latency and cost. Our agents make real decisions; we don't run a fake "user agent" mocking the requester.
7. **Synchronous global blackboard.** Orleans grain handles serialised access; we don't replicate Python's lock-free dict mutation pattern.
8. **Per-process file-system artifact store.** Fine for v1's single-host; v2 will move to a pluggable artifact store with optional blob backend.

### What we adopt from each system, in one line

- **ChatDev classic:** blackboard pattern, per-phase reset, OS-as-tester, ComposedPhase loops.
- **ChatDev 2.0 (DevAll):** evolutionary validation that linear-only is wrong; DAG runtime is right.
- **AutoGen:** N-agent group chat with composable termination; selector ladder (round-robin → LLM).
- **LangGraph:** stateful graph with checkpointers; reducers for shared state; subgraphs for composition.
- **CrewAI:** YAML-first agent + workflow definitions.
- **Magentic-One:** explicit progress/task ledger as first-class state; OS-as-truth for capability tools.

### Source paths for cross-reference

- ChatDev classic: `chatdev/chat_chain.py`, `chatdev/phase.py`, `chatdev/composed_phase.py`, `chatdev/chat_env.py`, `camel/agents/role_playing.py`, `CompanyConfig/Default/{ChatChainConfig,PhaseConfig,RoleConfig}.json`
- ChatDev 2.0: `workflow/graph.py`, `runtime/node/registry.py`, `yaml_instance/ChatDev_v1.yaml`
- AutoGen: `autogen_agentchat.teams.GroupChatManager`, `SelectorGroupChat`, `Swarm`
- LangGraph: `langgraph.graph.StateGraph`, `langgraph.checkpoint.MemorySaver`
- CrewAI: `crewai.Crew`, `crewai.Task`, `crewai.agents.cache`
- Magentic-One: `autogen-magentic-one/src/autogen_magentic_one/orchestrator.py` (Orchestrator + ledger templates)
