using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Mcp.Client;
using Archer.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Client;

namespace Archer.Mcp.Tests;

public class McpToolAdapterTests
{
    [Fact]
    public void Composes_wire_name_from_server_and_tool_names()
    {
        var adapter = MakeAdapter("trello", "list_boards");

        adapter.Name.Should().Be("trello__list_boards");
        adapter.ServerName.Should().Be("trello");
        adapter.ServerToolName.Should().Be("list_boards");
        ToolNaming.ToLogical(adapter.Name).Should().Be("trello.list_boards");
    }

    [Fact]
    public void Rejects_composed_name_that_exceeds_wire_regex()
    {
        var act = () => MakeAdapter("trello", "name.with.dots");

        act.Should().Throw<ArgumentException>().WithMessage("*not valid*");
    }

    [Fact]
    public void Definition_uses_provided_description_and_parameters()
    {
        var schema = new JsonObject { ["type"] = "object" };

        var adapter = MakeAdapter("trello", "list_boards",
            description: "Lists Trello boards.",
            parameters: schema);

        adapter.Definition.Description.Should().Be("Lists Trello boards.");
        adapter.Definition.Parameters["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public async Task ExecuteAsync_returns_failed_result_when_pool_throws()
    {
        var pool = new ThrowingPool(new InvalidOperationException("server down"));
        var adapter = new McpToolAdapter(pool, "trello", "list_boards", "desc", []);

        var result = await adapter.ExecuteAsync(new ToolRequest(
            ToolCallId: "call-1",
            ToolName: "trello__list_boards",
            Arguments: [],
            RepoRoot: "/tmp",
            AgentId: "agent-1"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("server down");
        result.ToolCallId.Should().Be("call-1");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_user_cancellation()
    {
        var pool = new ThrowingPool(new OperationCanceledException());
        var adapter = new McpToolAdapter(pool, "trello", "list_boards", "desc", []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await adapter.ExecuteAsync(new ToolRequest(
            "call-1", "trello__list_boards", [], "/tmp", "agent-1"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static McpToolAdapter MakeAdapter(
        string server,
        string toolName,
        string description = "",
        JsonObject? parameters = null)
    {
        var pool = new ThrowingPool(new InvalidOperationException("not exercised"));
        return new McpToolAdapter(pool, server, toolName, description, parameters ?? []);
    }

    private sealed class ThrowingPool : IMcpClientPool
    {
        private readonly Exception _ex;
        public ThrowingPool(Exception ex) => _ex = ex;
        public ValueTask<McpClient> GetAsync(string serverName, CancellationToken cancellationToken = default)
            => throw _ex;
        public ValueTask EvictAsync(string serverName, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
