using System.Text.Json.Nodes;
using Archer.Domain.Time;
using Archer.Domain.Tools;
using Archer.Persistence;
using Archer.Tools;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class TodoListToolTests
{
    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private static (TodoListTool tool, InMemoryAgentBlobStore store, FixedClock clock) Sut()
    {
        var store = new InMemoryAgentBlobStore();
        var clock = new FixedClock();
        return (new TodoListTool(store, clock), store, clock);
    }

    private static ToolRequest Req(string op, JsonObject? extra = null)
    {
        var args = new JsonObject { ["operation"] = op };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                args[kv.Key] = kv.Value?.DeepClone();
            }
        }
        return new ToolRequest(
            ToolCallId: "call_" + op,
            ToolName: "todo_list",
            Arguments: args,
            RepoRoot: "/tmp",
            AgentId: "agent_TESTAGENTXXX1");
    }

    [Fact]
    public void Definition_exposes_required_schema()
    {
        var (tool, _, _) = Sut();
        tool.Name.Should().Be("todo_list");
        tool.Definition.Name.Should().Be("todo_list");
        tool.Definition.Parameters["required"]!.AsArray()[0]!.GetValue<string>().Should().Be("operation");
    }

    [Fact]
    public async Task Missing_operation_fails()
    {
        var (tool, _, _) = Sut();
        var req = new ToolRequest("c1", "todo_list", [], "/tmp", "agent_TESTAGENTXXX2");
        var r = await tool.ExecuteAsync(req);
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("operation");
    }

    [Fact]
    public async Task Unknown_operation_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("unknown"));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("Unknown operation");
    }

    [Fact]
    public async Task Add_creates_a_todo_and_persists_it()
    {
        var (tool, store, _) = Sut();
        var r = await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "investigate auth", ["notes"] = "n" }));

        r.Success.Should().BeTrue();
        r.ResultItemCount.Should().Be(1);

        var saved = await store.LoadAsync<TodoListTool.TodoList>("agent_TESTAGENTXXX1", "todos");
        saved!.Items.Should().HaveCount(1);
        saved.Items[0].Title.Should().Be("investigate auth");
        saved.Items[0].Notes.Should().Be("n");
    }

    [Fact]
    public async Task Add_without_title_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("add"));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("title");
    }

    [Fact]
    public async Task List_returns_all_items()
    {
        var (tool, _, _) = Sut();
        await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "a" }));
        await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "b" }));

        var r = await tool.ExecuteAsync(Req("list"));
        r.Success.Should().BeTrue();
        r.Data["todos"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_changes_fields_and_advances_timestamp()
    {
        var (tool, _, clock) = Sut();
        var add = await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "old" }));
        var id = add.Data["todos"]!.AsArray()[0]!["id"]!.GetValue<string>();

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var r = await tool.ExecuteAsync(Req("update", new JsonObject
        {
            ["id"] = id,
            ["title"] = "new",
            ["notes"] = "more",
            ["status"] = "doing",
        }));

        r.Success.Should().BeTrue();
        var item = r.Data["todos"]!.AsArray()[0]!;
        item["title"]!.GetValue<string>().Should().Be("new");
        item["notes"]!.GetValue<string>().Should().Be("more");
        item["status"]!.GetValue<string>().Should().Be("doing");
    }

    [Fact]
    public async Task Update_without_id_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("update"));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("id");
    }

    [Fact]
    public async Task Update_unknown_id_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("update", new JsonObject { ["id"] = "no-such" }));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("not found");
    }

    [Fact]
    public async Task Update_invalid_status_returns_failed_tool_result()
    {
        var (tool, _, _) = Sut();
        var add = await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "x" }));
        var id = add.Data["todos"]!.AsArray()[0]!["id"]!.GetValue<string>();

        var r = await tool.ExecuteAsync(Req("update", new JsonObject { ["id"] = id, ["status"] = "bogus" }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Complete_marks_done()
    {
        var (tool, _, _) = Sut();
        var add = await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "x" }));
        var id = add.Data["todos"]!.AsArray()[0]!["id"]!.GetValue<string>();

        var r = await tool.ExecuteAsync(Req("complete", new JsonObject { ["id"] = id }));
        r.Success.Should().BeTrue();
        r.Data["todos"]!.AsArray()[0]!["status"]!.GetValue<string>().Should().Be("done");
    }

    [Fact]
    public async Task Complete_without_id_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("complete"));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Complete_unknown_id_fails()
    {
        var (tool, _, _) = Sut();
        var r = await tool.ExecuteAsync(Req("complete", new JsonObject { ["id"] = "missing" }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Clear_removes_blob()
    {
        var (tool, store, _) = Sut();
        await tool.ExecuteAsync(Req("add", new JsonObject { ["title"] = "x" }));
        var r = await tool.ExecuteAsync(Req("clear"));
        r.Success.Should().BeTrue();

        var saved = await store.LoadAsync<TodoListTool.TodoList>("agent_TESTAGENTXXX1", "todos");
        saved.Should().BeNull();
    }
}
