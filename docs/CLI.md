## Archer CLI Reference

The `archer` command is the primary terminal interface to the Archer agent framework.
It is a thin shell over the same Orleans host the [TUI](./TUI.md) uses
(`src/Archer.Cli/Hosting/CliHost.cs`); every subcommand boots the host, talks to
the appropriate grain, and streams `AgentEvent`s back to stdout via
`EventRenderer` (`src/Archer.Cli/Rendering/EventRenderer.cs:8`).

For configuration sources (`appsettings.json`, env vars, `CODESCOUT_*` prefix)
see [CONFIGURATION.md](./CONFIGURATION.md). For OpenTelemetry wiring, see
[TELEMETRY.md](./TELEMETRY.md). For agent profiles, see `agents/*.yaml`.

Subcommands are registered in `src/Archer.Cli/Program.cs:4-13`. Running `archer`
with no subcommand falls through to the interactive REPL attached at
`Program.cs:15`.

### Global options

These are added as **global** options by `InteractiveCommand.Attach`
(`src/Archer.Cli/Commands/InteractiveCommand.cs:23-28`) and apply to every
subcommand:

| Flag         | Default                | Meaning                                                          |
|--------------|------------------------|------------------------------------------------------------------|
| `--repo`     | current working dir    | Repo root the agent investigates (`InteractiveCommand.cs:15`)    |
| `--agent-id` | none                   | Resume a specific agent instead of creating one                  |
| `--model`    | per-agent default      | Override Azure OpenAI deployment                                 |
| `--state-dir`| `.archer`              | Override `Persistence:StateDirectory` (`CommonOptions.cs:14`)    |

`--state-dir` works for every subcommand; the override is materialized by
`CommonOptions.StateDirOverride` (`CommonOptions.cs:23-27`) which mutates
configuration before the host is built.

### `archer new` — create a new agent

```
archer new --repo <path> --prompt <text> [--agent <id>] [--agent-id <instance-id>] [--model <deployment>] [--state-dir <dir>]
```

Source: `src/Archer.Cli/Commands/NewCommand.cs:35`.

| Flag           | Default      | Required | Meaning                                                    |
|----------------|--------------|----------|------------------------------------------------------------|
| `--repo`, `-r` | —            | yes      | Path to the repo to investigate (`NewCommand.cs:17-22`)    |
| `--prompt`,`-p`| —            | yes      | First user message                                         |
| `--agent`      | `code-scout` | no       | Agent type from `agents/*.yaml` (`NewCommand.cs:32`)       |
| `--agent-id`   | auto         | no       | Explicit instance id; must satisfy `AgentId.IsValid`       |
| `--model`      | per-agent    | no       | Override deployment for this turn                          |

Behaviour:

1. Resolves the agent type via `IAgentDefinitionRegistry.Get` and exits with
   code `2` if unknown (`NewCommand.cs:45-49`).
2. Mints a new `AgentId` (or validates `--agent-id`).
3. Subscribes to the agent's event stream **before** initializing the grain
   (`NewCommand.cs:67-71`) so no events are lost.
4. Calls `IArcherAgentGrain.InitializeAsync(NewAgentRequest)` and waits up to
   10 minutes for `TurnCompletedEvent` or `TurnFailedEvent`
   (`WaitForTurnEndAsync` at `NewCommand.cs:100-109`).

Exit codes: `0` success, `2` invalid agent type/id, non-zero on host failure.

Streamed events (rendered by `EventRenderer.Render`,
`src/Archer.Cli/Rendering/EventRenderer.cs:13-60`): `[turn:NNNN] started`,
`[model] thinking (deployment)`, `[tool] name {args}`, `[tool] name completed/failed`,
`[reasoning] ...`, `[summary] ...`, `Final: …`, `[turn] complete` or `[turn] failed`.

Example:

```
archer new --repo ~/code/myproj \
           --prompt "Find the auth middleware and explain the token flow" \
           --agent code-scout
```

### `archer resume` / `archer ask` — continue an existing agent

```
archer resume <agent-id> --prompt <text> [--state-dir <dir>]
archer ask    <agent-id> --prompt <text>
archer resume <agent-id> "<prompt>"        # positional form
```

Source: `src/Archer.Cli/Commands/ResumeCommand.cs:13`. `ask` is registered as a
second build of the same command (`Program.cs:7-8`); both behave identically.

| Argument / flag          | Default | Required                        | Meaning                          |
|--------------------------|---------|---------------------------------|----------------------------------|
| `agent-id` (positional)  | —       | yes                             | Existing agent id                |
| `--prompt`, `-p`         | —       | one of `--prompt` or positional | New user message                 |
| `inline-prompt` positional | —     | one of `--prompt` or positional | Same prompt, positional          |

Validates `AgentId.IsValid` (`ResumeCommand.cs:44-49`) and rejects empty prompts
with exit code `2` (`ResumeCommand.cs:38-43`). Streams the same events as `new`
and exits when the turn ends.

Example:

```
archer resume scout_a1b2c3d4e5f6 -p "Now show me where this is called from"
archer ask    scout_a1b2c3d4e5f6 "Same question, positional form"
```

### `archer status <agent-id>` — print snapshot

Source: `src/Archer.Cli/Commands/StatusCommand.cs:11`.

Prints a one-shot summary loaded via `IArcherAgentGrain.GetSnapshotAsync`
(`StatusCommand.cs:26`). Output (`StatusCommand.cs:33-51`):

```
Agent:           scout_xxxxxxxxxxxx
Repo:            /path/to/repo
Created:         2026-04-25T12:34:56Z
Updated:         …
Messages:        12
Latest seq:      11
Active turn:     (none)
Model:           gpt-5.3-codex

Todos (3):
  [Doing  ] todo_1  Investigate auth middleware
  [Done   ] todo_2  …

Recent summaries (3):
  - …
```

Exits `2` if the agent has not been initialized (`StatusCommand.cs:27-31`).

### `archer events <agent-id> [--follow]` — stream events

Source: `src/Archer.Cli/Commands/EventsCommand.cs:11`.

| Flag             | Default | Meaning                                                  |
|------------------|---------|----------------------------------------------------------|
| `--follow`, `-f` | off     | Keep streaming after `TurnCompleted`/`TurnFailed`        |

Without `--follow` the command exits as soon as it sees a terminal event
(`EventsCommand.cs:30-37`). With it, it runs until Ctrl-C. All events go through
the same renderer as `new`/`resume`.

Example:

```
archer events scout_a1b2c3d4e5f6 --follow
```

### `archer list` — show known agent ids

Source: `src/Archer.Cli/Commands/ListCommand.cs:11`. Lists every agent
persisted by `IAgentStateStore.ListAgentsAsync` (`ListCommand.cs:21-31`). Prints
`(no agents found)` when the state directory is empty. Useful when you've lost
track of your `scout_*` ids.

### `archer agents` — list registered agent definitions

Source: `src/Archer.Cli/Commands/AgentsCommand.cs:11`. Reads
`IAgentDefinitionRegistry.All` (the YAMLs loaded from `./agents/`) and prints a
table:

```
ID                   DEPLOYMENT           TOOLS                          DESCRIPTION
code-scout           gpt-5.3-codex        list_files,grep,search_pattern Investigates a local code repo …
```

Column widths and truncation logic at `AgentsCommand.cs:30-37`. If no YAML is
registered, prints `(no agent definitions registered — drop a YAML in ./agents/)`.

### Interactive REPL — `archer` with no subcommand

Source: `src/Archer.Cli/Commands/InteractiveCommand.cs:30-110`.

Starts a line-oriented REPL using whatever `--repo`, `--agent-id`, `--model` and
`--state-dir` you passed. The prompt is `> ` for a fresh session, or
`<id6>>` once an agent is active (`InteractiveCommand.cs:67`). Anything that
doesn't start with `/` is sent as a user message — the REPL initializes a new
agent on the first non-slash line if `--agent-id` was not supplied
(`InteractiveCommand.cs:92-98`).

Slash commands (handler at `InteractiveCommand.cs:112-188`):

| Command                | Effect                                                                                            |
|------------------------|---------------------------------------------------------------------------------------------------|
| `/help`                | Show inline help (`InteractiveCommand.cs:127-136`)                                                |
| `/status`              | Print one-line snapshot (`/status`, `InteractiveCommand.cs:138-151`)                              |
| `/new <prompt>`        | Abandon active agent, mint a new one with the given prompt (`InteractiveCommand.cs:153-164`)      |
| `/interrupt [reason]`  | Send `InterruptRequest` to active agent — supersedes the live turn (`InteractiveCommand.cs:166-175`) |
| `/clear`               | `Console.Clear()`                                                                                 |
| `/exit`, `/quit`       | Leave the REPL                                                                                    |

Examples:

```
$ archer --repo ~/code/myproj
Archer — interactive mode.
Repo: /Users/me/code/myproj
> Find the auth middleware
[turn:0001] started
[model] thinking (gpt-5.3-codex)
[tool] list_files {"path":"."}
[tool] list_files completed: 47 entries
[reasoning] Looking for middleware modules…
Final: The auth middleware lives in …
[turn] complete
a1b2c3> /interrupt user changed mind
[agent] interrupt requested
a1b2c3> /new look at the database layer instead
[agent] new scout_b2c3d4e5f6a7
…
```

The REPL reads from a thread-pool task so Ctrl-C cancels cleanly even while
blocked on stdin (`InteractiveCommand.cs:236-247`). Long turns are bounded by a
15-minute pump timeout (`InteractiveCommand.cs:210-215`).

### Exit-code summary

| Code | Meaning                                                              |
|------|----------------------------------------------------------------------|
| 0    | Command succeeded                                                    |
| 2    | Bad input (invalid agent id, unknown agent type, missing prompt, missing repo) |
| other| Host or grain failure surfaced via the host                          |

### See also

- [TUI.md](./TUI.md) — the same workflows in a Terminal.Gui front-end
- [TELEMETRY.md](./TELEMETRY.md) — wiring traces and metrics for the CLI host
- [CONFIGURATION.md](./CONFIGURATION.md) — host config sources and load order
- ARCHITECTURE.md *(TODO — not yet written; will cover grain layout, event sink, persistence)*
