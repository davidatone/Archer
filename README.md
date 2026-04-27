# Archer

Actor-based, YAML-driven agent framework for .NET. Build composable, interruptible LLM
agents that reason, call tools, and coordinate over a Microsoft Orleans actor model — with
streaming events, durable per-agent state, hot-reload of agent profiles, and OpenTelemetry
out of the box.

> ⚠ **Status:** prototype, but well-tested. ~9.3k LOC of source, 436 tests across
> eight test projects, ~84% line coverage / ~76% branch coverage, and a green
> SonarCloud quality gate (0 bugs / 0 vulnerabilities / 0 code smells / 0 hotspots /
> A across reliability, security, and maintainability). The plumbing is solid
> (Domain/Application/Infrastructure separation, fenced-turn semantics, atomic-write
> persistence, percentage-based context windows, OTel spans/metrics/logs, MCP with
> non-blocking startup) but the surface area is intentionally small — one reference
> agent (`code-scout`) over Azure OpenAI's Responses API, four built-in tools, four
> MCP integrations (`memory`, `trello`, `simplemem`, `atlassian`), a CLI and a
> Terminal.Gui TUI.

## Why this exists

Most agent frameworks treat each LLM call as a single in-process function. That breaks
down the moment you want any of:

- A user typing a follow-up while a turn is mid-flight (need fencing, not cancellation
  alone).
- Many agents talking to many other agents (need actor identity + per-actor state).
- Resumable, persistent sessions (state survives a process restart).
- Pluggable agent personalities without recompiling (YAML drops into a folder; reloads
  live).

Archer composes Microsoft Orleans grains, Microsoft.Extensions.AI's chat client
abstraction (over Azure OpenAI Responses), and Terminal.Gui v2 to address all four. The
core is generic — point it at any chat model and any set of tools.

## Quickstart

```bash
# 1. Build
dotnet build Archer.slnx

# 2. Drop your Azure OpenAI key into a gitignored dev-settings file
cp src/Archer.Cli/appsettings.Development.json.example \
   src/Archer.Cli/appsettings.Development.json
$EDITOR src/Archer.Cli/appsettings.Development.json

# 3a. Run the CLI
dotnet run --project src/Archer.Cli -- \
  new --repo ./samples/sample-repo --prompt "Where is auth implemented?"

# 3b. Or the TUI (opens in iTerm2/Terminal.app)
scripts/tui-debug.sh --repo ./samples/sample-repo

# 3c. Or under .NET Aspire — gets you a dashboard at https://localhost:17181
#     with traces, logs, and metrics already wired up
dotnet run --project src/Aspire
```

See [docs/CLI.md](docs/CLI.md), [docs/TUI.md](docs/TUI.md), or
[docs/ASPIRE.md](docs/ASPIRE.md) for the full surface.

## Architecture at a glance

```
┌─ User ──────────┐                        ┌─ Azure OpenAI ─┐
│  archer / TUI   │                        │  Responses API │
└────────┬────────┘                        └────────▲───────┘
         │ commands / events                        │
┌────────▼─────────────────────────────────────────┼───────────────────┐
│  Orleans silo (in-process)                       │                   │
│  ┌──────────────────┐    ┌────────────────────┐  │                   │
│  │ ArcherAgentGrain │◄───┤  TurnWorkerGrain   ├──┘                   │
│  │  durable state   │    │  per-turn loop     │   IModelTurnRunner   │
│  └────────▲─────────┘    └────────┬───────────┘                      │
│           │ fence checks          │                                  │
│  ┌────────┴────────┐      ┌───────▼─────────┐  ┌─────────────────┐   │
│  │ FileStateStore  │      │  ToolRegistry   │  │ AgentDefinition │   │
│  │  state.json     │      │  4 built-ins    │  │  Registry       │   │
│  │  events.ndjson  │      │  list/grep/...  │  │  YAML hot-reload│   │
│  └─────────────────┘      └─────────────────┘  └─────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
                                   │
                          OTel traces/logs/metrics
                                   ▼
                       Aspire dashboard or any OTLP collector
```

Full C4 diagrams and the per-turn sequence flow:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## What an "agent" is, here

A YAML file. Drop one into `agents/`, the registry hot-loads it, and the CLI's
`--agent <id>` flag (or the TUI's New-agent dialog) lets users pick it.

```yaml
# agents/code-scout.yaml
id: code-scout
description: Investigates a local code repository.

model:
  deployment: gpt-5.3-codex
  contextWindow: 200000
  maxCompletionTokens: 65536
  reasoning:
    effort: high
    summary: auto

instructions: |
  You are Code Scout — inspect code via tools, never guess.
  Cite file:line in your answer.

tools:
  - list_files
  - grep
  - search_pattern
  - todo_list

context:
  recentMessageWindow: 30%   # of contextWindow tokens, rounded up
  pinFirstMessage: true

interruption: hard
```

Schema reference: [docs/AGENT_DEFINITIONS.md](docs/AGENT_DEFINITIONS.md).

## Project structure

```
src/
  Aspire/                 Archer.AppHost — .NET Aspire dashboard + OTel collector
  Core/
    Archer.Domain/        entities, events, requests (no deps)
    Archer.Application/   interfaces only (ports)
  Infrastructure/
    Archer.Persistence/   file state store + YAML loader + hot-reload
    Archer.Events/        Channel-based pub/sub + persisting decorator
    Archer.Tools/         list_files, grep, search_pattern, todo_list
    Archer.Model/         Azure OpenAI Responses runner + IChatClientFactory
  Hosting/
    Archer.Actors/        Orleans grains (ArcherAgentGrain, TurnWorkerGrain)
    Archer.Host/          silo wiring, OTel registration, env-var binding
  Presentation/
    Archer.Cli/           binary `archer` (System.CommandLine + REPL)
    Archer.Tui/           binary `archer-tui` (Terminal.Gui v2)
agents/
  code-scout.yaml         the reference agent profile
tests/                    Tools, Persistence, Actors, Tui smoke tests
docs/                     this index → component-level deep dives
samples/                  sample repo for the agent to investigate
scripts/                  tui-debug.sh (launches TUI in iTerm2/Terminal.app)
```

Layering is enforced bottom-up: Domain → Application → infrastructure (Persistence /
Events / Tools / Model) → Actors / Host → Presentation. Domain has zero NuGet deps; the
TUI doesn't reach past Application interfaces.

## Documentation

| Doc | What's in it |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | C4 (context/container/component) + sequence + state diagrams + layering rules + fence semantics |
| [INTERNALS.md](docs/INTERNALS.md) | Turn lifecycle, event flow, persistence layout, model-runner protocol, hot-reload |
| [AGENT_DEFINITIONS.md](docs/AGENT_DEFINITIONS.md) | YAML schema, validation, percentage-based context windows, hot reload |
| [TOOLS.md](docs/TOOLS.md) | Built-in tools (`list_files`, `grep`, `search_pattern`, `todo_list`) + adding new |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | `appsettings.json` sections + env vars + Azure OpenAI endpoint shapes |
| [CLI.md](docs/CLI.md) | `archer` command reference (new / resume / status / events / agents / list / REPL) |
| [TUI.md](docs/TUI.md) | `archer-tui` keybindings, panes, Markdown rendering, iTerm2 mouse notes, Servers dialog |
| [MEMORY.md](docs/MEMORY.md) | The four memory layers (transcript / recent-window / summaries / blob store) + the MCP knowledge-graph server |
| [TELEMETRY.md](docs/TELEMETRY.md) | OTel ActivitySource, Meter, instruments, tag keys, dashboards |
| [ASPIRE.md](docs/ASPIRE.md) | Running under .NET Aspire — one command, dashboard included |
| [CONTRIBUTING.md](docs/CONTRIBUTING.md) | Build, test, coverage, Sonar workflow + coding conventions |
| [proposals/multi-agent-sdlc/](docs/proposals/multi-agent-sdlc/README.md) | RFC: layering a workflow runtime on top of agents — solo / critic / peer-swarm modes, multi-repo workspace, ChatDev-style SDLC |

## Key design choices

- **Two-key turn fence.** A turn is identified by `(ActiveTurnId, ActiveTurnStartedAtSeq)`.
  When a new user message arrives mid-flight, both keys roll forward; the in-flight
  worker's commit is rejected. Cancellation is best-effort; correctness comes from the
  fence. ([Internals → Turn fencing](docs/INTERNALS.md))

- **Per-agent grains, fresh worker per turn.** `ArcherAgentGrain` is keyed by agent id
  and owns durable state. `TurnWorkerGrain` is keyed by `Guid TurnId` — a fresh grain
  per turn, no `[Reentrant]` games, no shared mutable state across turns.

- **Domain has no infrastructure dependencies.** State, events, tool requests, model
  inputs are all POCOs. Orleans serialization for grain calls is registered as
  `System.Text.Json` over the `Archer.Domain` and `Archer.Application` namespaces in
  the host — no `[GenerateSerializer]` pollution.

- **Tools are pure functions over a request envelope.** `ITool.ExecuteAsync` takes
  a `ToolRequest` and returns a `ToolResult`. Path-traversal escapes are blocked by
  `RepoPathResolver`; binary files and oversized files are skipped; secret patterns are
  redacted.

- **YAML, not code, defines an agent.** Different agent profiles ≠ different binaries.
  Drop a YAML, `IAgentDefinitionRegistry` watches the directory, the next turn picks up
  the new instructions / tools / model.

- **Percentage-based context window.** `recentMessageWindow: 30%` means "30% of the
  model's input-token budget", rounded up to the next message — so an `appsettings`
  change that swaps a 200k-token model for a 1M-token model rebalances automatically.

- **OTel is first-class.** `ArcherTelemetry` exposes a single `ActivitySource` and
  `Meter`. The host registers OTLP exporters for traces, metrics, AND logs. Under
  Aspire it auto-resolves; outside Aspire any OTLP backend works.

## Running tests

```bash
dotnet test Archer.slnx
```

Today: 16 tests across `Tools`, `Persistence`, and `Actors`. The TUI's layout is
verified by a separate `dotnet run --project src/Archer.Tui -- --check-layout` headless
mode that boots `Terminal.Gui.FakeDriver`, dumps the cell buffer, and exits.

## Contributing / extending

Common tasks, with pointers:

- **Add a new agent type** → drop a YAML in `agents/`. See
  [AGENT_DEFINITIONS.md](docs/AGENT_DEFINITIONS.md#adding-a-new-agent-type).
- **Add a new tool** → implement `ITool` and register in `AddArcherTools`. See
  [TOOLS.md → Adding a new tool](docs/TOOLS.md).
- **Wire a different LLM provider** → implement `IChatClientFactory` (in
  `Archer.Application.Model`) and replace the registration in
  `ModelServiceCollectionExtensions`.
- **Different state backend** → implement `IAgentStateStore`. The default
  `FileAgentStateStore` is a single 200-line file.
- **Run telemetry into your own collector** → set `Otel:Endpoint` in
  `appsettings.json`. See [TELEMETRY.md](docs/TELEMETRY.md).

## License

TBD.
