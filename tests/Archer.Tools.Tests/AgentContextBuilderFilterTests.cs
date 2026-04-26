using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Tools;
using Archer.Model.AgentFramework;
using Archer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Tools.Tests;

/// <summary>
/// Verifies the per-agent tool whitelist semantics in <see cref="AgentContextBuilder"/>.
/// New rules (post-MCP):
///   <list type="bullet">
///     <item><c>[]</c> = no tools.</item>
///     <item>list contains <c>"*"</c> = all currently-registered tools.</item>
///     <item><c>"&lt;server&gt;.*"</c> or <c>"&lt;server&gt;__*"</c> = all tools whose wire name has that prefix.</item>
///     <item>exact name (logical or wire) = match the corresponding tool.</item>
///   </list>
/// </summary>
public class AgentContextBuilderFilterTests
{
    [Fact]
    public void Empty_tools_list_means_no_tools()
    {
        var builder = NewBuilder("list_files", "trello__list_boards");

        var input = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: []));

        input.Tools.Should().BeEmpty();
    }

    [Fact]
    public void Star_wildcard_means_all_tools()
    {
        var builder = NewBuilder("list_files", "trello__list_boards", "simplemem__add_dialogue");

        var input = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: ["*"]));

        input.Tools.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "list_files", "trello__list_boards", "simplemem__add_dialogue" });
    }

    [Fact]
    public void Server_wildcard_with_dot_form_matches_all_tools_for_that_server()
    {
        var builder = NewBuilder("list_files", "trello__list_boards", "trello__create_card", "simplemem__recall");

        var input = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: ["trello.*"]));

        input.Tools.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "trello__list_boards", "trello__create_card" });
    }

    [Fact]
    public void Server_wildcard_with_underscore_form_also_matches()
    {
        var builder = NewBuilder("trello__list_boards", "trello__create_card");

        var input = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: ["trello__*"]));

        input.Tools.Should().HaveCount(2);
    }

    [Fact]
    public void Explicit_name_matches_in_either_logical_or_wire_form()
    {
        var builder = NewBuilder("trello__list_boards", "trello__create_card");

        var withDot = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: ["trello.list_boards"]));
        var withUnderscore = builder.Build(MinimalState(), Guid.NewGuid(), DefinitionWith(tools: ["trello__list_boards"]));

        withDot.Tools.Select(t => t.Name).Should().BeEquivalentTo(new[] { "trello__list_boards" });
        withUnderscore.Tools.Select(t => t.Name).Should().BeEquivalentTo(new[] { "trello__list_boards" });
    }

    [Fact]
    public void Mixed_wildcards_and_explicit_names_compose_with_union_semantics()
    {
        var builder = NewBuilder("list_files", "trello__list_boards", "trello__create_card", "simplemem__recall");

        var input = builder.Build(MinimalState(), Guid.NewGuid(),
            DefinitionWith(tools: ["list_files", "trello.*"]));

        input.Tools.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "list_files", "trello__list_boards", "trello__create_card" });
    }

    [Fact]
    public void Newly_registered_tools_are_visible_on_the_next_turn_for_matching_wildcards()
    {
        var registry = new ToolRegistry([new FakeTool("list_files")], NullLogger<ToolRegistry>.Instance);
        var builder = new AgentContextBuilder(registry);
        var def = DefinitionWith(tools: ["trello.*"]);

        var before = builder.Build(MinimalState(), Guid.NewGuid(), def);
        before.Tools.Should().BeEmpty();

        // Simulate an MCP server connecting after agent definition was loaded.
        registry.Add(new FakeTool("trello__list_boards"));

        var after = builder.Build(MinimalState(), Guid.NewGuid(), def);
        after.Tools.Select(t => t.Name).Should().Contain("trello__list_boards");
    }

    // ---- helpers -----------------------------------------------------------------------

    private static AgentContextBuilder NewBuilder(params string[] toolWireNames)
    {
        var tools = toolWireNames.Select(n => (ITool)new FakeTool(n)).ToList();
        var registry = new ToolRegistry(tools, NullLogger<ToolRegistry>.Instance);
        return new AgentContextBuilder(registry);
    }

    private static AgentState MinimalState() => new()
    {
        AgentId = "test-agent",
        RepoRoot = "/tmp",
        AgentDefinitionId = "code-scout",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static AgentDefinition DefinitionWith(IReadOnlyList<string> tools) => new()
    {
        Id = "code-scout",
        Description = "test",
        Instructions = "test",
        Tools = tools,
        Model = new ModelProfile { Deployment = "gpt-x", ContextWindowTokens = 8000 },
        Context = new ContextProfile(),
    };

    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public ToolDefinition Definition { get; } = new(name, "fake", []);
        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult(request.ToolCallId, request.ToolName, true, [], "ok"));
    }
}
