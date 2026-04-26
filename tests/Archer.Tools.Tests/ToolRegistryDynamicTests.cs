using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Tools.Tests;

public class ToolRegistryDynamicTests
{
    [Fact]
    public void Constructor_seeds_with_initial_tools()
    {
        var reg = new ToolRegistry([new FakeTool("list_files")], NullLogger<ToolRegistry>.Instance);

        reg.Tools.Should().HaveCount(1);
        reg.TryGet("list_files", out _).Should().BeTrue();
    }

    [Fact]
    public void Add_inserts_tool_and_fires_changed()
    {
        var reg = new ToolRegistry([], NullLogger<ToolRegistry>.Instance);
        ToolRegistryChange? captured = null;
        reg.Changed += c => captured = c;

        reg.Add(new FakeTool("trello__list_boards"));

        reg.Tools.Should().HaveCount(1);
        reg.TryGet("trello__list_boards", out _).Should().BeTrue();
        captured!.Kind.Should().Be(ToolRegistryChangeKind.Added);
    }

    [Fact]
    public void TryGet_accepts_either_logical_or_wire_form()
    {
        var reg = new ToolRegistry([new FakeTool("trello__list_boards")], NullLogger<ToolRegistry>.Instance);

        reg.TryGet("trello__list_boards", out var t1).Should().BeTrue();
        reg.TryGet("trello.list_boards", out var t2).Should().BeTrue();
        ReferenceEquals(t1, t2).Should().BeTrue();
    }

    [Fact]
    public void Remove_returns_true_when_present_and_fires_changed()
    {
        var reg = new ToolRegistry([new FakeTool("trello__list_boards")], NullLogger<ToolRegistry>.Instance);
        ToolRegistryChange? captured = null;
        reg.Changed += c => captured = c;

        var removed = reg.Remove("trello.list_boards");

        removed.Should().BeTrue();
        reg.Tools.Should().BeEmpty();
        captured!.Kind.Should().Be(ToolRegistryChangeKind.Removed);
    }

    [Fact]
    public void Remove_returns_false_when_absent_and_does_not_fire()
    {
        var reg = new ToolRegistry([], NullLogger<ToolRegistry>.Instance);
        var fired = false;
        reg.Changed += _ => fired = true;

        reg.Remove("trello.list_boards").Should().BeFalse();
        fired.Should().BeFalse();
    }

    [Fact]
    public void Add_rejects_invalid_wire_names()
    {
        var reg = new ToolRegistry([], NullLogger<ToolRegistry>.Instance);

        var act = () => reg.Add(new FakeTool("trello.list_boards"));    // dot not allowed in wire form

        act.Should().Throw<ArgumentException>().WithMessage("*not a valid wire name*");
    }

    [Fact]
    public void Definitions_reflect_current_snapshot_after_mutations()
    {
        var reg = new ToolRegistry([new FakeTool("a")], NullLogger<ToolRegistry>.Instance);
        reg.Definitions.Select(d => d.Name).Should().BeEquivalentTo(new[] { "a" });

        reg.Add(new FakeTool("b"));
        reg.Definitions.Select(d => d.Name).Should().BeEquivalentTo(new[] { "a", "b" });

        reg.Remove("a");
        reg.Definitions.Select(d => d.Name).Should().BeEquivalentTo(new[] { "b" });
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public ToolDefinition Definition { get; } = new(name, "fake", []);
        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult(request.ToolCallId, request.ToolName, true, [], "ok"));
    }
}
