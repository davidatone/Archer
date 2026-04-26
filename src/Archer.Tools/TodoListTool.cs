using System.Diagnostics;
using System.Text.Json.Nodes;
using Archer.Application.Persistence;
using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Time;
using Archer.Domain.Tools;

namespace Archer.Tools;

/// <summary>
/// A simple per-agent task tracker. Persists through <see cref="IAgentBlobStore"/> — by default
/// in-process memory; the durable audit lives in the agent's NDJSON event log via the
/// ToolCallCompletedEvent that fires after every operation here. The agent's system prompt
/// describes how to use it (see agents/code-scout.yaml).
/// </summary>
public sealed class TodoListTool : ITool
{
    private const string BlobName = "todos";

    private readonly IAgentBlobStore _store;
    private readonly ISystemClock _clock;

    public TodoListTool(IAgentBlobStore store, ISystemClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Name => "todo_list";

    public ToolDefinition Definition { get; } = new(
        Name: "todo_list",
        Description: "Create, update, complete, and list investigation todos for the current agent.",
        Parameters: JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "operation": { "type": "string", "enum": ["add", "update", "complete", "list", "clear"] },
            "id": { "type": "string" },
            "title": { "type": "string" },
            "notes": { "type": "string" },
            "status": { "type": "string", "enum": ["todo", "doing", "done", "blocked"] }
          },
          "required": ["operation"],
          "additionalProperties": false
        }
        """)!.AsObject());

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var op = request.Arguments["operation"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(op))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "operation is required.", sw.Elapsed);
        }

        try
        {
            var current = await _store.LoadAsync<TodoList>(request.AgentId, BlobName, cancellationToken)
                          ?? new TodoList();

            return op switch
            {
                "list" => Render(current.Items, "Listed todos.", request, sw),
                "add" => await HandleAdd(current, request, sw, cancellationToken),
                "update" => await HandleUpdate(current, request, sw, cancellationToken),
                "complete" => await HandleComplete(current, request, sw, cancellationToken),
                "clear" => await HandleClear(request, sw, cancellationToken),
                _ => ToolResult.Failed(request.ToolCallId, Name, $"Unknown operation: {op}", sw.Elapsed),
            };
        }
        catch (ArgumentException ex)
        {
            sw.Stop();
            return ToolResult.Failed(request.ToolCallId, Name, ex.Message, sw.Elapsed);
        }
    }

    private async Task<ToolResult> HandleAdd(TodoList current, ToolRequest request, Stopwatch sw, CancellationToken ct)
    {
        var title = request.Arguments["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(title))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "title is required for add.", sw.Elapsed);
        }
        var notes = request.Arguments["notes"]?.GetValue<string>();
        var now = _clock.UtcNow;
        var todo = new TodoItem
        {
            Id = TodoItem.NewId(),
            Title = title,
            Notes = notes,
            Status = TodoStatus.Todo,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        current.Items.Add(todo);
        await _store.SaveAsync(request.AgentId, BlobName, current, ct);
        return Render([todo], $"Added todo {todo.Id}.", request, sw);
    }

    private async Task<ToolResult> HandleUpdate(TodoList current, ToolRequest request, Stopwatch sw, CancellationToken ct)
    {
        var id = request.Arguments["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "id is required for update.", sw.Elapsed);
        }
        var todo = current.Items.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (todo is null)
        {
            return ToolResult.Failed(request.ToolCallId, Name, $"Todo {id} not found.", sw.Elapsed);
        }
        var title = request.Arguments["title"]?.GetValue<string>();
        var notes = request.Arguments["notes"]?.GetValue<string>();
        var statusStr = request.Arguments["status"]?.GetValue<string>();
        if (title is not null) { todo.Title = title; }
        if (notes is not null) { todo.Notes = notes; }
        if (statusStr is not null) { todo.Status = Enum.Parse<TodoStatus>(statusStr, ignoreCase: true); }
        todo.UpdatedAtUtc = _clock.UtcNow;
        await _store.SaveAsync(request.AgentId, BlobName, current, ct);
        return Render([todo], $"Updated todo {id}.", request, sw);
    }

    private async Task<ToolResult> HandleComplete(TodoList current, ToolRequest request, Stopwatch sw, CancellationToken ct)
    {
        var id = request.Arguments["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "id is required for complete.", sw.Elapsed);
        }
        var todo = current.Items.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (todo is null)
        {
            return ToolResult.Failed(request.ToolCallId, Name, $"Todo {id} not found.", sw.Elapsed);
        }
        todo.Status = TodoStatus.Done;
        todo.UpdatedAtUtc = _clock.UtcNow;
        await _store.SaveAsync(request.AgentId, BlobName, current, ct);
        return Render([todo], $"Completed todo {id}.", request, sw);
    }

    private async Task<ToolResult> HandleClear(ToolRequest request, Stopwatch sw, CancellationToken ct)
    {
        await _store.DeleteAsync(request.AgentId, BlobName, ct);
        return Render([], "Cleared all todos.", request, sw);
    }

    private static ToolResult Render(
        List<TodoItem> items,
        string message,
        ToolRequest request,
        Stopwatch sw)
    {
        sw.Stop();
        var arr = new JsonArray();
        foreach (var t in items)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = t.Title,
                ["notes"] = t.Notes,
                ["status"] = t.Status.ToString().ToLowerInvariant(),
                ["createdAtUtc"] = t.CreatedAtUtc,
                ["updatedAtUtc"] = t.UpdatedAtUtc,
            });
        }

        var data = new JsonObject
        {
            ["message"] = message,
            ["todos"] = arr,
        };

        return new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: request.ToolName,
            Success: true,
            Data: data,
            Summary: message,
            ResultItemCount: items.Count,
            Duration: sw.Elapsed);
    }

    /// <summary>JSON envelope persisted in the blob store. Just a list of items today;
    /// the wrapper makes it easy to add metadata (per-agent prefs, schema version, …) later.</summary>
    public sealed class TodoList
    {
        public List<TodoItem> Items { get; set; } = [];
    }
}
