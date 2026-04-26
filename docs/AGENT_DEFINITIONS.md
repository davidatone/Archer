## Archer Agent Definitions

An *agent definition* is a declarative YAML profile that names what an agent
does, which model it talks to, which tools it can use, and how it manages
context. Multiple instances can be spawned from a single definition; they share
the profile but each has its own conversation state.

For host-level configuration (Azure OpenAI connection, telemetry, logging) see
[CONFIGURATION.md](./CONFIGURATION.md).

The schema is parsed by hand — see
[`src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs:11-109`](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L11-L109)
— and projected onto the records in
[`src/Archer.Domain/Agents/AgentDefinition.cs:8-113`](../src/Archer.Domain/Agents/AgentDefinition.cs#L8-L113).

### Where YAML lives

The host registers two directories at startup
([`ArcherHostBuilder.cs:64-72`](../src/Archer.Host/ArcherHostBuilder.cs#L64-L72)),
in priority order (first wins on `id` collision):

1. `<cwd>/agents/` — your repo's working-copy YAMLs.
2. `<AppContext.BaseDirectory>/agents/` — files copied next to the build
   output (e.g. `bin/Debug/net10.0/agents/`).

Only `*.yaml` files are scanned, top-level only (no recursion) — see
[`AgentDefinitionRegistry.cs:67`](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L67).

### Top-level schema

| Key            | Type    | Required | Default              | Notes |
| -------------- | ------- | -------- | -------------------- | ----- |
| `id`           | string  | yes      | —                    | Unique within the registry. Used as the lookup key from the CLI/TUI. Throws `InvalidDataException("Missing required field 'id'.")` if absent ([loader line 36, 78](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L36)). |
| `description`  | string  | no       | `""` (empty string)  | Free-text. Shown in agent pickers. Loader uses `TryGetString` and falls back to empty ([line 37](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L37)). |
| `model`        | mapping | yes      | —                    | See [model block](#model-block). Throws `InvalidDataException("Missing required mapping 'model'.")` if absent ([line 40, 96](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L40)). |
| `instructions` | string  | yes      | —                    | The system prompt. Throws on missing ([line 38](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L38)). |
| `tools`        | list    | yes      | —                    | List of tool names. Throws `InvalidDataException("Missing required list 'tools'.")` if absent or non-sequence ([line 39, 101-108](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L101-L108)). |
| `context`      | mapping | no       | defaults for every field | See [context block](#context-block). Whole block can be omitted; loader builds `new ContextProfile()` ([line 41](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L41)). |
| `interruption` | string  | no       | `Hard`               | Currently only `Hard` is implemented; see [interruption](#interruption). Parsed via `Enum.Parse<InterruptionMode>(..., ignoreCase: true)` — an unknown value throws `ArgumentException` ([line 42-44](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L42-L44)). |

When any required field is missing the registry's `TryUpsertFile` catches the
exception and logs `LogError("Failed to load agent definition from {Path}", ...)`
([`AgentDefinitionRegistry.cs:140-144`](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L140-L144))
— the file is skipped, other agents still load, and Archer keeps running.

### `model` block

Maps to [`ModelProfile`](../src/Archer.Domain/Agents/AgentDefinition.cs#L19-L31)
via [`ParseModel`](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L48-L55).

| Key                   | Type    | Required | Default    | Notes |
| --------------------- | ------- | -------- | ---------- | ----- |
| `deployment`          | string  | yes      | —          | Azure OpenAI deployment / model name. Required ([line 50](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L50)). |
| `apiVersion`          | string  | no       | `null`     | Falls back to `AzureOpenAI:ApiVersion`, then SDK default. |
| `contextWindow`       | int     | no       | `null`     | Model's input-context budget in tokens. Used to size percentage-based `recentMessageWindow`. `null` ⇒ implementation-defined default ([`AgentDefinition.cs:24-26`](../src/Archer.Domain/Agents/AgentDefinition.cs#L24-L26)). |
| `maxCompletionTokens` | int     | no       | `16384`    | Cap on output tokens for this agent. Loader fallback at [line 53](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L53). |
| `reasoning`           | mapping | no       | `null`     | Optional reasoning controls; see below. |
| `reasoning.effort`    | string  | no       | `Medium`   | One of `None`, `Minimal`, `Low`, `Medium`, `High` ([`AgentDefinition.cs:39-46`](../src/Archer.Domain/Agents/AgentDefinition.cs#L39-L46)). Parsed case-insensitively; unknown values throw `ArgumentException` ([loader line 60](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L60)). |
| `reasoning.summary`   | string  | no       | `Auto`     | One of `Auto`, `Concise`, `Detailed` ([`AgentDefinition.cs:48-53`](../src/Archer.Domain/Agents/AgentDefinition.cs#L48-L53)). Same parse rules ([loader line 63](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L63)). |

A non-integer `maxCompletionTokens` silently falls back to `16384`
(`TryGetInt` returns `null` on parse failure — see
[loader lines 85-88](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L85-L88)).
A bad enum value, by contrast, **throws** and the file is rejected.

### `context` block

Maps to [`ContextProfile`](../src/Archer.Domain/Agents/AgentDefinition.cs#L55-L66)
via [`ParseContext`](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L67-L73).

| Key                   | Type           | Required | Default | Notes |
| --------------------- | -------------- | -------- | ------- | ----- |
| `recentMessageWindow` | percentage \| decimal | no | `30%`   | Budget for recent-message inclusion as a share of `model.contextWindow`. Rounded *up* to the next message boundary ([`AgentDefinition.cs:97`](../src/Archer.Domain/Agents/AgentDefinition.cs#L97)). |
| `pinFirstMessage`     | bool           | no       | `true`  | Always include the first user message even if it falls outside the window ([`AgentDefinition.cs:65`](../src/Archer.Domain/Agents/AgentDefinition.cs#L65)). |

`recentMessageWindow` accepts two equivalent forms, parsed by
[`Percentage.Parse`](../src/Archer.Domain/Agents/AgentDefinition.cs#L101-L112):

- Percentage with trailing `%`: `"30%"`, `"75%"`. Must be in `[0, 100]`;
  outside that range `Percentage.Of` throws `ArgumentOutOfRangeException`
  ([line 90-93](../src/Archer.Domain/Agents/AgentDefinition.cs#L90-L93)).
- Decimal fraction: `"0.30"` (≤ 1.0 is treated as a fraction; > 1.0 is treated
  as a percentage and re-validated, [line 111](../src/Archer.Domain/Agents/AgentDefinition.cs#L111)).

So `30%`, `0.30`, and `30` all mean the same thing. With a 200 000-token
`contextWindow`, that's `Math.Ceiling(0.30 * 200_000) = 60_000` tokens
([`Percentage.RoundUp`](../src/Archer.Domain/Agents/AgentDefinition.cs#L97)).

If the whole `context` block is omitted, the loader supplies `new
ContextProfile()` ([line 41](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L41)),
which gives `recentMessageWindow: 30%` and `pinFirstMessage: true`
([`AgentDefinition.cs:62-65`](../src/Archer.Domain/Agents/AgentDefinition.cs#L62-L65)).

### `tools`

A YAML sequence of strings. Each entry must match the `Name` of a tool
registered in DI by
[`ToolsServiceCollectionExtensions.AddArcherTools`](../src/Archer.Tools/ToolsServiceCollectionExtensions.cs#L27-L30).
The four tools that ship with Archer today:

| Name             | Source                                              | Purpose |
| ---------------- | --------------------------------------------------- | ------- |
| `list_files`     | [`ListFilesTool.cs:22`](../src/Archer.Tools/ListFilesTool.cs#L22)         | Enumerate files under a path. |
| `grep`           | [`GrepTool.cs:22`](../src/Archer.Tools/GrepTool.cs#L22)                   | Search a single file/glob with regex. |
| `search_pattern` | [`SearchPatternTool.cs:22`](../src/Archer.Tools/SearchPatternTool.cs#L22) | Repo-wide ranked search (uses `Tools:PreferredPathPrefixes`). |
| `todo_list`     | [`TodoListTool.cs:18`](../src/Archer.Tools/TodoListTool.cs#L18)            | Long-horizon task tracking within a turn. |

Empty-string and whitespace entries are dropped silently
([loader line 107](../src/Archer.Persistence/Agents/YamlAgentDefinitionLoader.cs#L107)).
Names that don't match a registered tool aren't validated by the loader — the
agent will fail at the first tool call when the registry can't resolve them.

### `interruption`

Maps to [`InterruptionMode`](../src/Archer.Domain/Agents/AgentDefinition.cs#L72-L75)
— an enum with a single value, `Hard`. The default is `Hard` (loader line 44),
so omitting the field is identical to `interruption: hard`. Other policies
("soft", "queue", ...) are intentionally not implemented; passing one will throw
`ArgumentException` from `Enum.Parse`.

### Hot reload

[`AgentDefinitionRegistry`](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs)
keeps the registry live:

1. **Initial scan** ([lines 57-73](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L57-L73))
   — both directories are walked top-level and every `*.yaml` is loaded
   via `YamlAgentDefinitionLoader.LoadFile`.
2. **FileSystemWatcher** ([lines 75-104](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L75-L104))
   — one watcher per existing directory, listening for `Created`, `Changed`,
   `Renamed`, and `Deleted`. Filter is `*.yaml`, `NotifyFilter` covers
   `LastWrite | FileName | CreationTime | Size`.
3. **250 ms debounce** ([line 15](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L15)
   and the debouncer at [lines 106-131](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L106-L131))
   — editors emit a flurry of events on save; per-path `CancellationTokenSource`s
   coalesce them into one reload.
4. **First-write-wins by directory order** ([lines 150-159](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L150-L159))
   — if both directories define the same `id`, the one with the lower
   `dirIndex` (i.e. `<cwd>/agents/`) keeps the entry; the other is logged at
   `Debug` and ignored.
5. **`Changed` event** ([line 33](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L33))
   — fires `Action?` after every successful upsert or remove. The TUI
   subscribes to refresh its agent picker without restart.

Bad YAML doesn't crash the registry: the exception is logged and the previous
entry (if any) stays in place
([lines 140-144](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L140-L144)).
On delete the entry is only removed when the path matches and the `dirIndex`
matches — so deleting a shadowed copy in the bin/ directory won't yank the
working-copy version
([lines 183-187](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L183-L187)).

### Examples

#### Annotated: `agents/code-scout.yaml`

The agent that ships with the framework. Every supported field is exercised:

```yaml
# Required: unique key, used by `archer new --agent code-scout`.
id: code-scout

# Free-text description shown in pickers; can be omitted.
description: |
  Investigates a local code repository. Reads files, searches for patterns, and produces
  cited findings with file:line references. Read-only — never modifies source.

# Required model block.
model:
  # Azure OpenAI deployment / model name. Falls back to AzureOpenAI:DefaultDeployment.
  deployment: gpt-5.3-codex
  # Optional API version override (else AzureOpenAI:ApiVersion).
  apiVersion: 2025-04-01-preview
  # Token budget for the model's input window — feeds the percentage math below.
  contextWindow: 200000
  # Cap on this agent's output tokens; default 16384 if omitted.
  maxCompletionTokens: 65536
  # Optional reasoning sub-block.
  reasoning:
    effort: high     # None | Minimal | Low | Medium | High
    summary: auto    # Auto | Concise | Detailed

# Required system prompt.
instructions: |
  You are Code Scout, a local repository investigation agent.
  ...

# Required list. Each name must match a registered tool.
tools:
  - list_files
  - grep
  - search_pattern
  - todo_list

# Optional context block. Defaults: 30% / pinFirstMessage: true.
context:
  recentMessageWindow: 30%   # 0.30 also works; 60_000 tokens of a 200k window
  pinFirstMessage: true

# Optional. Currently only "hard" is implemented; default is "hard".
interruption: hard
```

This file lives at
[`agents/code-scout.yaml`](../agents/code-scout.yaml).

#### Minimal: `agents/explainer.yaml`

A stub showing only the required fields. The omitted blocks fall back to
defaults: 16k completion tokens, no reasoning sub-block, 30% recent-message
window pinning the first user message, hard interruption.

```yaml
id: explainer
description: Explains code in plain English without modifying anything.

model:
  deployment: gpt-5-mini

instructions: |
  You are Explainer. Use the provided tools to read code and produce
  a plain-English summary. Cite file:line for every claim.

tools:
  - list_files
  - grep
```

Drop this in `agents/explainer.yaml` and the registry picks it up within
~250 ms.

### How to add a new agent type

1. **Drop a YAML in `agents/`** at the repo root (or any directory that's on
   the host's scan list — see
   [`ArcherHostBuilder.cs:64-68`](../src/Archer.Host/ArcherHostBuilder.cs#L64-L68)).
   At a minimum, set `id`, `description`, `model.deployment`, `instructions`,
   and `tools`.
2. **Reference its `id`** from the CLI's `archer new --agent <id>` flag
   ([`NewCommand.cs:32`](../src/Archer.Cli/Commands/NewCommand.cs#L32) — the
   default is `code-scout`) or from the TUI's *New agent* dialog. Both
   resolve through `IAgentDefinitionRegistry.Get(id)` and emit a clear
   "Agent definition '<id>' is not registered" error if it's missing
   ([`TurnWorkerGrain.cs:97-101`](../src/Archer.Actors/Grains/TurnWorkerGrain.cs#L97-L101)).
3. **The registry hot-reloads.** No restart needed — new file → load, edit →
   reload (250 ms debounce), delete → unload, all surfaced via the
   `Changed` event ([line 33](../src/Archer.Persistence/Agents/AgentDefinitionRegistry.cs#L33)).
   If the YAML is malformed the previous version (if any) stays in place and
   the error is logged at `Error` level.

### See also

- [CONFIGURATION.md](./CONFIGURATION.md) — host configuration: Azure OpenAI
  connection, persistence directory, tool limits, telemetry, logging.
