## Archer Tools

Built-in tools shipped with the Archer agent framework, plus how to register your
own. Tools are the model's only mechanism for side-effects: every model turn that
isn't a final answer is a sequence of tool calls executed by the
`TurnWorkerGrain` (see [INTERNALS.md](./INTERNALS.md)).

A tool implements `ITool`
([`src/Archer.Application/Tools/ITool.cs:5-10`](../src/Archer.Application/Tools/ITool.cs#L5-L10)):

```csharp
public interface ITool
{
    string Name { get; }
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
```

`ToolDefinition` carries the JSON-Schema for parameters; `ToolRequest` carries
the parsed `Arguments`, the `RepoRoot` to sandbox against, and the `AgentId`
(see [`src/Archer.Domain/Tools/ToolRequest.cs`](../src/Archer.Domain/Tools/ToolRequest.cs)).

The four built-in tools all live in `src/Archer.Tools/` and are registered by
`AddArcherTools` in
[`ToolsServiceCollectionExtensions.cs:27-30`](../src/Archer.Tools/ToolsServiceCollectionExtensions.cs#L27-L30).
The shared options bag is `ToolOptions`
([`ToolOptions.cs`](../src/Archer.Tools/ToolOptions.cs)).

### Tool result envelope

Every tool returns a `ToolResult` with a uniform shape
([`src/Archer.Domain/Tools/ToolResult.cs`](../src/Archer.Domain/Tools/ToolResult.cs)):

```jsonc
{
  "ToolCallId": "call_abc",
  "ToolName": "list_files",
  "Success": true,
  "Data": { /* tool-specific JSON object */ },
  "Summary": "12 entries under src",
  "ResultItemCount": 12,
  "Duration": "00:00:00.0420000",
  "Error": null
}
```

Failure uses `ToolResult.Failed(...)` which sets `Success=false`, `Data={}`, and
puts the message in both `Summary` and `Error`
([`ToolResult.cs:15-16`](../src/Archer.Domain/Tools/ToolResult.cs#L15-L16)).

---

### `list_files`

**Source:** [`src/Archer.Tools/ListFilesTool.cs`](../src/Archer.Tools/ListFilesTool.cs)

Enumerate files and directories under a repo-relative path. Optionally recursive
and glob-filtered.

**Parameters schema** (extracted from `Definition` at
[`ListFilesTool.cs:24-40`](../src/Archer.Tools/ListFilesTool.cs#L24-L40)):

```json
{
  "type": "object",
  "properties": {
    "path":         { "type": "string", "description": "Path relative to repository root." },
    "recursive":    { "type": "boolean" },
    "maxResults":   { "type": "integer" },
    "includeGlobs": { "type": "array", "items": { "type": "string" } },
    "excludeGlobs": { "type": "array", "items": { "type": "string" } }
  },
  "required": ["path"],
  "additionalProperties": false
}
```

**Defaults** (from `ExecuteAsync` at lines 46-50):

| Argument       | Default                              |
| -------------- | ------------------------------------ |
| `path`         | `"."`                                |
| `recursive`    | `false`                              |
| `maxResults`   | `200`                                |
| `includeGlobs` | none                                 |
| `excludeGlobs` | `ToolOptions.DefaultExcludeGlobs`    |

`DefaultExcludeGlobs` is `[".git/**", "bin/**", "obj/**", "node_modules/**", "dist/**", "build/**", "coverage/**", "*.min.js", "*.lock", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.ico", "*.pdf", "*.zip"]`
([`ToolOptions.cs:8-26`](../src/Archer.Tools/ToolOptions.cs#L8-L26)).

**Safety rules:**

- `IRepoPathResolver.TryResolve` runs first and rejects absolute or
  escape-attempting paths (lines 52-55) — see [Path resolver](#path-resolver-irepopathresolver).
- Symlinks (`FileAttributes.ReparsePoint`) are skipped unless
  `ToolOptions.FollowSymlinks` is `true` (lines 82-85).
- A glob include/exclude `Microsoft.Extensions.FileSystemGlobbing.Matcher` is
  built once and consulted for recursive listings (lines 68, 87-93).
- Hard cap at `maxResults`; when exceeded, enumeration stops and
  `truncated: true` is set in the result (lines 74-78, 108).

**Example request:**

```json
{
  "ToolCallId": "call_01",
  "ToolName": "list_files",
  "Arguments": {
    "path": "src/Archer.Tools",
    "recursive": true,
    "maxResults": 50,
    "includeGlobs": ["**/*.cs"]
  },
  "RepoRoot": "/repo",
  "AgentId": "scout-1"
}
```

**Example success result (`Data` shape, lines 104-109):**

```json
{
  "path": "src/Archer.Tools",
  "entries": [
    { "type": "file", "path": "src/Archer.Tools/GrepTool.cs",  "sizeBytes": 5120 },
    { "type": "dir",  "path": "src/Archer.Tools/Safety",      "sizeBytes": 0 }
  ],
  "truncated": false
}
```

**Error envelope** (e.g. when the path escapes the repo root):

```json
{ "Success": false, "Error": "Path escapes repository root.", "Data": {} }
```

Other failure modes: `"Path is not a directory: ..."` (lines 59-62), and any
error returned by `RepoPathResolver` (empty repo root, absolute path, escape).

---

### `grep`

**Source:** [`src/Archer.Tools/GrepTool.cs`](../src/Archer.Tools/GrepTool.cs)

Regex search a single file with surrounding context lines. Always single-file —
use `search_pattern` for cross-repo searches.

**Parameters schema** ([`GrepTool.cs:24-40`](../src/Archer.Tools/GrepTool.cs#L24-L40)):

```json
{
  "type": "object",
  "properties": {
    "file":          { "type": "string" },
    "pattern":       { "type": "string" },
    "caseSensitive": { "type": "boolean" },
    "contextLines":  { "type": "integer" },
    "maxMatches":    { "type": "integer" }
  },
  "required": ["file", "pattern"],
  "additionalProperties": false
}
```

**Defaults** (lines 54-56):

| Argument        | Default | Notes                              |
| --------------- | ------- | ---------------------------------- |
| `caseSensitive` | `false` |                                    |
| `contextLines`  | `2`     | Floor of 0                         |
| `maxMatches`    | `50`    | Floor of 1                         |

**Safety rules:**

- Repo-relative path resolution via `IRepoPathResolver` (line 58).
- File-size guard: rejects files larger than `ToolOptions.MaxFileBytes`
  (1 MB default, lines 69-75).
- Binary-file guard: `BinaryDetector.LooksBinaryFile` reads up to 4096 bytes;
  any NUL byte or >30% non-printable bytes ⇒ refuse
  ([`BinaryDetector.cs:5-26`](../src/Archer.Tools/Safety/BinaryDetector.cs#L5-L26)).
- Regex compile uses a 2-second timeout
  (`RegexOptions.Compiled | CultureInvariant`, line 90); compile errors are
  reported as `"Invalid regex: ..."` (line 94).
- Every match line and its context are passed through `SecretRedactor.Redact`
  (lines 119, 124, 130) which substitutes `[redacted-secret]` for things that
  look like API keys/tokens/connection strings (12+ char value after `key=` /
  `token:` / etc.) and `[redacted-private-key]` for PEM blocks
  ([`SecretRedactor.cs:7-13`](../src/Archer.Tools/Safety/SecretRedactor.cs#L7-L13)).

**Example request:**

```json
{
  "Arguments": {
    "file": "src/Archer.Tools/GrepTool.cs",
    "pattern": "Redact",
    "contextLines": 1,
    "maxMatches": 10
  }
}
```

**Example success `Data`:**

```json
{
  "file": "src/Archer.Tools/GrepTool.cs",
  "matches": [
    {
      "line": 119,
      "text": "                before.Add(SecretRedactor.Redact(lines[b]));",
      "before": ["            var before = new JsonArray();"],
      "after":  ["            }"]
    }
  ],
  "truncated": false
}
```

**Error envelopes:**

- `"file and pattern are required."` (line 51) — missing args.
- `"File not found: <file>"` (line 65).
- `"File too large (N bytes > MAX)."` (lines 71-74).
- `"Refusing to grep a binary file."` (line 79).
- `"Invalid regex: ..."` (line 94).

---

### `search_pattern`

**Source:** [`src/Archer.Tools/SearchPatternTool.cs`](../src/Archer.Tools/SearchPatternTool.cs)

Regex search across many files, ranking results by a heuristic score so the most
likely-relevant files appear first.

**Parameters schema** ([`SearchPatternTool.cs:24-43`](../src/Archer.Tools/SearchPatternTool.cs#L24-L43)):

```json
{
  "type": "object",
  "properties": {
    "pattern":           { "type": "string" },
    "path":              { "type": "string" },
    "includeGlobs":      { "type": "array", "items": { "type": "string" } },
    "excludeGlobs":      { "type": "array", "items": { "type": "string" } },
    "caseSensitive":     { "type": "boolean" },
    "maxFiles":          { "type": "integer" },
    "maxMatchesPerFile": { "type": "integer" },
    "contextLines":      { "type": "integer" }
  },
  "required": ["pattern"],
  "additionalProperties": false
}
```

**Defaults** (lines 55-62):

| Argument            | Default                            |
| ------------------- | ---------------------------------- |
| `path`              | `"."`                              |
| `caseSensitive`     | `false`                            |
| `maxFiles`          | `50`                               |
| `maxMatchesPerFile` | `5`                                |
| `contextLines`      | `1`                                |
| `includeGlobs`      | none                               |
| `excludeGlobs`      | `ToolOptions.DefaultExcludeGlobs`  |

**Safety rules:** identical to `grep` — `MaxFileBytes` guard (line 104),
symlink skip when `!FollowSymlinks` (108), `BinaryDetector` (112), regex
timeout (81), `SecretRedactor.Redact` on every snippet (152). Plus glob-driven
file enumeration via `GlobMatcher`
([`GlobMatcher.cs`](../src/Archer.Tools/GlobMatcher.cs)) which delegates to
`Microsoft.Extensions.FileSystemGlobbing.Matcher`.

**Ranking — `ScoreFile`** (lines 205-230):

```
score =
    0.35 * nameRelevance     // file has '.' in its name → 0.5, else 0.3
  + 0.25 * pathRelevance     // path starts with one of ToolOptions.PreferredPathPrefixes → 1.0, else 0.6
  + 0.20 * density           // matches per ~25-line bucket, capped at 1.0
  + 0.10 * sourcePriority    // 0.0 if path/name contains "test", else 1.0
  + 0.10 * (1 / (1 + log10(1 + totalLines)))  // mild bias toward shorter files
```

`PreferredPathPrefixes` defaults to `["src/", "lib/", "app/"]`
([`ToolOptions.cs:32-38`](../src/Archer.Tools/ToolOptions.cs#L32-L38)) and is
configurable via `Tools:PreferredPathPrefixes`. Test files are penalized so
implementation hits float to the top.

After scoring, results are sorted descending and truncated to `maxFiles`
(lines 167-170). The total file count surveyed is reported as `filesSearched`.

**Example request:**

```json
{
  "Arguments": {
    "pattern": "ToolResult\\.Failed",
    "path": ".",
    "includeGlobs": ["**/*.cs"],
    "maxFiles": 10
  }
}
```

**Example success `Data`** (lines 185-192):

```json
{
  "pattern": "ToolResult\\.Failed",
  "filesSearched": 132,
  "filesWithMatches": 4,
  "matches": [
    {
      "file": "src/Archer.Tools/GrepTool.cs",
      "score": 0.872,
      "matchCount": 6,
      "snippets": [
        { "line": 51, "text": "return ToolResult.Failed(request.ToolCallId, Name, ...);" }
      ]
    }
  ],
  "truncated": false
}
```

**Error envelopes:** `"pattern is required."` (line 52),
`"Path is not a directory: ..."` (line 70), plus the same regex / resolver
errors as `grep`.

---

### `todo_list`

**Source:** [`src/Archer.Tools/TodoListTool.cs`](../src/Archer.Tools/TodoListTool.cs)

Per-agent investigation todo list. Persists through the actor system so the
list survives across turns and process restarts.

**Parameters schema** ([`TodoListTool.cs:20-36`](../src/Archer.Tools/TodoListTool.cs#L20-L36)):

```json
{
  "type": "object",
  "properties": {
    "operation": { "type": "string", "enum": ["add", "update", "complete", "list", "clear"] },
    "id":        { "type": "string" },
    "title":     { "type": "string" },
    "notes":     { "type": "string" },
    "status":    { "type": "string", "enum": ["todo", "doing", "done", "blocked"] }
  },
  "required": ["operation"],
  "additionalProperties": false
}
```

**Operation matrix** (the `op switch` at lines 49-57):

| `operation` | Required args   | Effect                                              |
| ----------- | --------------- | --------------------------------------------------- |
| `list`      | —               | Return current todos.                               |
| `add`       | `title`         | Append a new `TodoItem` with `Status=Todo`.         |
| `update`    | `id` (+ any of `title`/`notes`/`status`) | Patch the todo; null fields untouched. |
| `complete`  | `id`            | Shortcut for `update` with `status=done`.           |
| `clear`     | —               | Drop all todos for this agent.                      |

**Persistence path:**

```
TodoListTool
  → IAgentTodoService (Application layer, [src/Archer.Application/Tools/IAgentTodoService.cs](../src/Archer.Application/Tools/IAgentTodoService.cs))
    → AgentTodoService (Actors layer, [src/Archer.Actors/AgentTodoService.cs](../src/Archer.Actors/AgentTodoService.cs))
      → IArcherAgentGrain.AddTodoAsync / UpdateTodoAsync / ListTodosAsync / ClearTodosAsync
        → AgentState.Todos  (List<TodoItem> — [src/Archer.Domain/Agents/AgentState.cs:17](../src/Archer.Domain/Agents/AgentState.cs#L17))
          → IAgentStateStore.SaveAsync  (atomic write of state.json)
```

Every mutating op is followed by `await _store.SaveAsync(_state)` in
[`ArcherAgentGrain.cs:244, 260, 271`](../src/Archer.Actors/Grains/ArcherAgentGrain.cs#L244-L271)
so todos are durable. The `AgentContextBuilder` then injects them into the next
turn's system prompt as a `Current TODOs:` block
([`AgentContextBuilder.cs:57-65`](../src/Archer.Model/AgentFramework/AgentContextBuilder.cs#L57-L65)).

**Example request — add:**

```json
{
  "Arguments": {
    "operation": "add",
    "title": "Trace AddUserMessageAsync fence path",
    "notes": "Compare ActiveTurnId vs LatestMessageSeq."
  }
}
```

**Example success `Data` (lines 119-145):**

```json
{
  "message": "Added todo 7f3a91c2.",
  "todos": [
    {
      "id": "7f3a91c2",
      "title": "Trace AddUserMessageAsync fence path",
      "notes": "Compare ActiveTurnId vs LatestMessageSeq.",
      "status": "todo",
      "createdAtUtc": "2026-04-25T17:14:02.331+00:00",
      "updatedAtUtc": "2026-04-25T17:14:02.331+00:00"
    }
  ]
}
```

**Error envelopes:** `"operation is required."` (line 44),
`"title is required for add."` (line 71), `"id is required for update."`
(line 83), `"id is required for complete."` (line 103),
`"Todo <id> not found."` (lines 93, 108), `"Unknown operation: <op>"` (line 56).

---

## Path resolver: `IRepoPathResolver`

**Source:** [`src/Archer.Tools/Safety/RepoPathResolver.cs`](../src/Archer.Tools/Safety/RepoPathResolver.cs)
(interface in
[`src/Archer.Application/Tools/IRepoPathResolver.cs`](../src/Archer.Application/Tools/IRepoPathResolver.cs))

Every filesystem-touching tool routes through this. It does two things:

1. **`NormalizeRelative`** (lines 72-97) coerces the model's many phrasings of
   "the project root" into a single canonical relative form:

   | Input       | Normalized form |
   | ----------- | --------------- |
   | `""`        | `""` (root)     |
   | `"."`       | `""`            |
   | `"./"`      | `""`            |
   | `"/"`       | `""`            |
   | `"./src"`   | `"src"`         |
   | `"/src"`    | `"src"`         |

   The intent is documented inline at lines 60-71: leading `./` is stripped, a
   leading `/` or `\` is treated as "from repo root" (not POSIX absolute). The
   escape-detection check below still catches any `..` or drive-letter
   shenanigans that survive normalization.

2. **`TryResolve`** (lines 16-58) sandboxes the result:
   - Empty `repoRoot` ⇒ `"Repository root is empty."`
   - After normalization, if the path is still rooted (`Path.IsPathRooted`)
     ⇒ `"Absolute paths are not allowed; provide a path relative to the repository root."`
   - Combine + `Path.GetFullPath` to canonicalize, then check the canonical
     path either equals the repo root *or* starts with `repoRoot +
     DirectorySeparator`. If neither holds ⇒ `"Path escapes repository root."`
     This catches `..` traversal even when surface-level normalization lets it
     through.

The result is that every tool can write `_paths.TryResolve(repoRoot, relPath, ...)`
once and trust the returned `fullPath` is inside the sandbox.

---

## Adding a new tool

Steps, with the actual wiring code:

**1. Implement `ITool`** in `Archer.Tools` (or any project that
references `Archer.Application.Tools` and `Archer.Domain.Tools`):

```csharp
using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Tools;

namespace Archer.Tools;

public sealed class CountLinesTool : ITool
{
    private readonly IRepoPathResolver _paths;

    public CountLinesTool(IRepoPathResolver paths) => _paths = paths;

    public string Name => "count_lines";

    public ToolDefinition Definition { get; } = new(
        Name: "count_lines",
        Description: "Count lines in a repo-relative file.",
        Parameters: JsonNode.Parse("""
        {
          "type": "object",
          "properties": { "file": { "type": "string" } },
          "required": ["file"],
          "additionalProperties": false
        }
        """)!.AsObject());

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        var file = request.Arguments["file"]?.GetValue<string>() ?? "";
        if (!_paths.TryResolve(request.RepoRoot, file, out var full, out var error))
            return ToolResult.Failed(request.ToolCallId, Name, error!);
        var lines = await File.ReadAllLinesAsync(full, ct);
        return new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: Name,
            Success: true,
            Data: new JsonObject { ["file"] = file, ["lines"] = lines.Length },
            Summary: $"{lines.Length} lines",
            ResultItemCount: lines.Length);
    }
}
```

**2. The `ToolDefinition`** is what the model sees — its `Parameters` JSON
object becomes the `JsonElement` schema attached to the
`ArcherFunctionDeclaration` in
[`AgentFrameworkModelTurnRunner.cs:233-237`](../src/Archer.Model/AgentFramework/AgentFrameworkModelTurnRunner.cs#L233-L237).
Keep the schema strict: set `additionalProperties: false`, list `required`
fields, and use `enum` where applicable so the model can't smuggle in unknown
arguments.

**3. Register in DI.** The simplest hook is to extend `AddArcherTools` (or
add your own extension method in your tool's project) and register the tool
with the same multi-binding pattern used today
([`ToolsServiceCollectionExtensions.cs:27-30`](../src/Archer.Tools/ToolsServiceCollectionExtensions.cs#L27-L30)):

```csharp
services.AddSingleton<ITool, ListFilesTool>();
services.AddSingleton<ITool, GrepTool>();
services.AddSingleton<ITool, SearchPatternTool>();
services.AddSingleton<ITool, TodoListTool>();
services.AddSingleton<ITool, CountLinesTool>();   // ← new
```

`ToolRegistry`'s constructor receives `IEnumerable<ITool>` and indexes
them by `Name`
([`ToolRegistry.cs:12-18`](../src/Archer.Tools/ToolRegistry.cs#L12-L18)) — no
other registration is required. Name collisions throw at startup
(`Dictionary.ToDictionary`).

If you prefer to package the tool in its own project, expose your own
`AddXyzTool(IServiceCollection)` extension that calls
`services.AddSingleton<ITool, MyTool>()` and document calling it
alongside `AddArcherTools` in the host wiring (see
[CONFIGURATION.md](./CONFIGURATION.md)).

**4. Whitelist on the agent.** Tools are filtered per-agent before reaching the
model
([`AgentContextBuilder.cs:116-124`](../src/Archer.Model/AgentFramework/AgentContextBuilder.cs#L116-L124)):
an empty `tools:` list means "all registered tools", a non-empty list is a
strict whitelist by `Name`. Reference your tool from the relevant agent YAML:

```yaml
id: code-scout
description: Code investigator
model:
  deployment: gpt-5
tools:
  - list_files
  - grep
  - search_pattern
  - todo_list
  - count_lines        # ← new
```

See [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md) for the full YAML schema and
hot-reload semantics.

**5. Verify wiring.** `ToolRegistry.ExecuteAsync` is the single dispatch point
([`ToolRegistry.cs:34-56`](../src/Archer.Tools/ToolRegistry.cs#L34-L56)).
Unknown tool names degrade to a `Failed` result rather than throwing, and
exceptions during `ExecuteAsync` are caught and converted to a `Failed`
envelope (lines 51-55), so a buggy tool can't take down the worker.

For metrics/spans, the worker already wraps each tool call in a
`archer.tool.<name>` `Activity` with `agent.id`, `turn.id`, `tool.name`,
`tool.call_id` tags and records `tool.calls` and `tool.duration_ms`
([`TurnWorkerGrain.cs:198-220`](../src/Archer.Actors/Grains/TurnWorkerGrain.cs#L198-L220)).
You don't need to add telemetry inside the tool — see
[TELEMETRY.md](./TELEMETRY.md) for what flows out.

---

## Cross-references

- [INTERNALS.md](./INTERNALS.md) — turn lifecycle, fence semantics, event flow.
- [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md) — agent YAML schema, hot-reload.
- [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level project layout.
- [CONFIGURATION.md](./CONFIGURATION.md) — host & connection wiring.
- [TELEMETRY.md](./TELEMETRY.md) — meters, traces, tags emitted.
