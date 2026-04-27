## Archer Memory

This page covers the memory layers an Archer agent uses: where conversation history
lives, what gets sent to the model on each turn, what survives a process restart, and
which knobs are tunable. For the agent profile that wires these together see
[AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md); for tools that read or write memory
explicitly see [TOOLS.md](./TOOLS.md).

### Five layers, four owners

| Layer | Owner | Persistence | Sent to the model? |
|-------|-------|------------|--------------------|
| 1. **Conversation transcript** (`Messages`) | per-agent grain | durable on disk | indirectly — see layer 2 |
| 2. **Recent-message window** | `AgentContextBuilder` | derived | yes — every turn |
| 3. **Summaries** | per-agent grain | durable on disk | latest summary only, prepended to system prompt |
| 4. **Per-agent blob store** | `IAgentBlobStore` | in-memory by default | no — agents call tools that read/write it |
| 5. **MCP knowledge graph** (external) | the `memory` MCP server | durable JSON file | no — agents call `memory.*` tools explicitly |

The first three are the agent's *own* memory, scoped to one agent id. The fourth is
side-storage for tool implementations (today only `todo_list`). The fifth is shared
across any agent whose tool whitelist allows it.

---

### 1. Conversation transcript — `AgentState.Messages`

Source: [`src/Archer.Domain/Agents/AgentState.cs:15`](../src/Archer.Domain/Agents/AgentState.cs#L15)

```csharp
public sealed class AgentState
{
    public List<AgentMessage> Messages { get; init; } = [];
    public long LatestMessageSeq { get; set; }
    // …
}
```

Every user message, every assistant final answer, and every tool result lands here
as an `AgentMessage` with a monotonically increasing `Seq`. The list is append-only
during normal operation; the only writers are `ArcherAgentGrain.AddUserMessageAsync`
and `ArcherAgentGrain.CommitFinalAnswerIfStillActiveAsync`
([`src/Archer.Actors/Grains/ArcherAgentGrain.cs:82, 181`](../src/Archer.Actors/Grains/ArcherAgentGrain.cs#L82)).

**Persistence.** `FileAgentStateStore` writes the state JSON to
`<repo>/.archer/agents/<agentId>/state.json` after every mutation
([`src/Archer.Persistence/FileAgentStateStore.cs`](../src/Archer.Persistence/FileAgentStateStore.cs)).
A separate NDJSON event log lives next to it. Both survive process restarts; they're
regular files in the repo's `.archer/` directory (which is `.gitignore`d).

**The transcript is not directly fed to the model.** The model gets a *recent slice*
selected by layer 2.

---

### 2. Recent-message window — `AgentContextBuilder.SelectRecentMessages`

Source: [`src/Archer.Model/AgentFramework/AgentContextBuilder.cs:66-101`](../src/Archer.Model/AgentFramework/AgentContextBuilder.cs#L66-L101)

Each turn the context builder picks the most recent slice of `Messages` whose
*estimated token cost* fits inside a percentage of the model's context window. The
percentage and the pin-policy are per-agent settings:

```yaml
# in agents/<id>.yaml
context:
  recent-message-window: "30%"   # default — see ContextProfile.RecentMessageWindow
  pin-first-message: true        # default — always include msg #1
```

Defaults: 30% of the model's context window, with the first user message pinned.
For a 128k-token model that's ~38k tokens of recent history. Older messages stay on
disk; the model just doesn't see them on this turn.

**Token estimate.** Cost is approximated as `Content.Length / 4` characters per token
([`AgentContextBuilder.cs:103-104`](../src/Archer.Model/AgentFramework/AgentContextBuilder.cs#L103-L104)).
This is rough but cheap; pricing this through a real tokenizer would require shipping
the model's BPE table.

**`Percentage`** is a domain primitive
([`AgentDefinition.cs:81-113`](../src/Archer.Domain/Agents/AgentDefinition.cs#L81-L113)),
parsed from either `"30%"` or `"0.30"`.

---

### 3. Summaries — `AgentState.Summaries`

Source: [`AgentState.cs:16`](../src/Archer.Domain/Agents/AgentState.cs#L16) +
[`AgentContextBuilder.cs:57-61`](../src/Archer.Model/AgentFramework/AgentContextBuilder.cs#L57-L61)

```csharp
public sealed record Summary
{
    public required Guid TurnId { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
```

A list of `Summary` records, one per `TurnId`. **Today the system prompt only includes
the latest one**, prepended as `Latest summary: <content>`:

```csharp
// AgentContextBuilder.BuildInstructions
if (state.Summaries.Count > 0)
{
    sb.AppendLine();
    sb.Append("Latest summary: ").AppendLine(state.Summaries[^1].Content);
}
```

**How summaries are produced.** An agent's turn emits a `SummaryEvent`
([`src/Archer.Domain/Events/AgentEvent.cs:45-49`](../src/Archer.Domain/Events/AgentEvent.cs#L45-L49));
the agent grain catches it in `AppendTurnEventAsync` and stores the content as a new
`Summary`
([`ArcherAgentGrain.cs:157-172`](../src/Archer.Actors/Grains/ArcherAgentGrain.cs#L157-L172)).

**There is no automatic summariser yet.** The plumbing exists — events flow, the grain
persists them, the context builder reads them — but no agent currently emits a
`SummaryEvent` on its own. If you wire an agent to summarise (e.g. with a "compact
this turn into one paragraph" tool), the surrounding machinery already supports it.

---

### 4. Per-agent blob store — `IAgentBlobStore`

Source: [`src/Archer.Application/Persistence/IAgentBlobStore.cs`](../src/Archer.Application/Persistence/IAgentBlobStore.cs)

```csharp
public interface IAgentBlobStore
{
    Task SaveAsync<T>(string agentId, string blobName, T payload, CancellationToken ct = default);
    Task<T?> LoadAsync<T>(string agentId, string blobName, CancellationToken ct = default);
    Task DeleteAsync(string agentId, string blobName, CancellationToken ct = default);
}
```

A typed key-value side-store keyed by `(agentId, blobName)`. **Used by tools** that
need state outside the message log; today only `TodoListTool` uses it:

```csharp
// src/Archer.Tools/TodoListTool.cs:19
private const string BlobName = "todos";

// SaveAsync(agentId, "todos", new TodoList { Items = ... })
```

**Default registration.** `PersistenceServiceCollectionExtensions.cs:29` registers
`InMemoryAgentBlobStore` — *blobs do not survive process restart by default*. There's
a file-backed implementation candidate but it isn't wired in yet. If your tool needs
durable side-storage today, wire a `FileAgentBlobStore` into DI yourself, or use the
MCP memory server (layer 5) instead.

The TUI side-pane reads `todos` directly via `AgentSession`
([`src/Archer.Tui/Sessions/AgentSession.cs:91-95`](../src/Archer.Tui/Sessions/AgentSession.cs#L91-L95))
to render the task list — that's the only consumer outside the tool itself.

---

### 5. MCP knowledge graph — external

Source: `mcp/memory.yaml` registers the official
[`@modelcontextprotocol/server-memory`](https://github.com/modelcontextprotocol/servers/tree/main/src/memory)
server over stdio.

```yaml
name: memory
transport:
  type: stdio
  command: npx
  args: ["-y", "@modelcontextprotocol/server-memory"]
  env:
    MEMORY_FILE_PATH: ${env:HOME}/.config/archer/memory.json
auth: { type: none }
```

**Storage shape.** A knowledge graph of:
- **entities** — typed nodes (e.g. `{ name: "atlassian.net :: Confluence page X", entityType: "page" }`)
- **relations** — typed edges between entities
- **observations** — free-text strings attached to entities

Persisted to a single JSON file (`MEMORY_FILE_PATH`, default
`~/.config/archer/memory.json`). Survives process restarts and survives across
worktrees, since it lives outside the repo.

**Tools exposed** (registered as `memory.<name>` / wire form `memory__<name>` per the
ToolNaming convention):

| Tool | Purpose |
|------|---------|
| `create_entities` | add nodes |
| `create_relations` | add edges |
| `add_observations` | attach text to a node |
| `delete_entities` / `delete_relations` / `delete_observations` | remove |
| `read_graph` | full graph dump |
| `search_nodes` | substring match across names + observations |
| `open_nodes` | fetch named nodes |

**Substring match — not vector / semantic.** `search_nodes` is a literal substring
filter; there's no embedding, no fuzzy matching. For exact-name recall ("the
letsqala.atlassian.net :: Confluence page Qala PLG Strategy" entity) it works well;
for "find anything similar to this concept" it doesn't.

**Access.** Any agent whose YAML `tools` whitelist includes `memory.*` can call these
tools. The agent has to *choose* to use them — there's no automatic
write-on-completion or auto-recall. Pattern in `code-scout.yaml`: store a structured
set of observations at the end of an investigation, recall them by exact entity name
on the next session.

**Lifetime.** The child process is spawned on first tool use by `McpClientPool` and
torn down at host shutdown. The JSON file is the durable artifact — back it up if
you care about it.

---

### What's *not* there yet

- **Vector / RAG memory.** No embeddings, no semantic search over the transcript.
  Recall by exact name (MCP) or last-N messages (window) only.
- **Cross-agent memory.** Two agents don't share `Messages` or `Summaries`. The MCP
  knowledge graph is the only durable medium that crosses agent boundaries.
- **Automatic summarisation.** No agent currently emits `SummaryEvent` on its own.
  Older messages drop off the recent-window slice and stay on disk, unaccessed.
- **Durable blob store by default.** `InMemoryAgentBlobStore` is registered; a file
  implementation can be slotted in via DI.

---

### Quick operational reference

| What you want | Where to look / what to do |
|---------------|----------------------------|
| Tail an agent's full transcript | `cat .archer/agents/<id>/events.ndjson` |
| Inspect persisted state | `cat .archer/agents/<id>/state.json` |
| Reset an agent | `rm -rf .archer/agents/<id>` (next run starts fresh) |
| Tune how much history goes to the model | `context.recent-message-window` in the agent YAML |
| Pin the first message even when it's old | `context.pin-first-message: true` (default) |
| Wipe MCP graph memory | `rm ~/.config/archer/memory.json` (the memory server recreates it on next call) |
| Dump the MCP graph | `archer mcp test memory` triggers a connect; `read_graph` is a tool the agent can call |

---

### See also

- [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md) — `context-profile` block and how it shapes the recent-window
- [ARCHITECTURE.md](./ARCHITECTURE.md) — the layered grain model that owns this state
- [INTERNALS.md](./INTERNALS.md) — the turn lifecycle that reads/writes the transcript
- [TOOLS.md](./TOOLS.md) — `todo_list` (uses blob store) + MCP tools (use the memory graph)
