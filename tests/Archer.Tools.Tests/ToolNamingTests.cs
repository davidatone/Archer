using Archer.Application.Tools;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class ToolNamingTests
{
    [Theory]
    [InlineData("trello.list_boards", "trello__list_boards")]
    [InlineData("simplemem.add_dialogue", "simplemem__add_dialogue")]
    [InlineData("list_files", "list_files")]                              // built-in, no separator
    [InlineData("trello.complex.tool_name", "trello__complex.tool_name")] // only first dot is replaced
    public void ToWire_replaces_first_dot_with_double_underscore(string logical, string expectedWire)
    {
        ToolNaming.ToWire(logical).Should().Be(expectedWire);
    }

    [Theory]
    [InlineData("trello__list_boards", "trello.list_boards")]
    [InlineData("list_files", "list_files")]
    [InlineData("snake_case_tool", "snake_case_tool")]                    // no double-underscore prefix
    public void ToLogical_replaces_first_double_underscore_with_dot(string wire, string expectedLogical)
    {
        ToolNaming.ToLogical(wire).Should().Be(expectedLogical);
    }

    [Fact]
    public void Round_trip_preserves_logical_form()
    {
        ToolNaming.ToLogical(ToolNaming.ToWire("trello.list_boards")).Should().Be("trello.list_boards");
    }

    [Theory]
    [InlineData("trello__list_boards", true)]
    [InlineData("list_files", true)]
    [InlineData("MixedCase-Allowed", true)]
    [InlineData("trello.list_boards", false)]                             // dot rejected
    [InlineData("trello list", false)]                                    // space rejected
    [InlineData("", false)]
    public void IsValidWireName_matches_OpenAI_function_name_regex(string name, bool valid)
    {
        ToolNaming.IsValidWireName(name).Should().Be(valid);
    }

    [Fact]
    public void IsValidWireName_rejects_names_over_64_characters()
    {
        var name = new string('a', 65);
        ToolNaming.IsValidWireName(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("trello.list_boards", "trello")]
    [InlineData("trello__list_boards", "trello")]
    [InlineData("list_files", null)]
    public void ServerPrefix_extracts_server_or_returns_null(string name, string? expected)
    {
        ToolNaming.ServerPrefix(name).Should().Be(expected);
    }

    [Fact]
    public void Compose_produces_a_wire_name()
    {
        var wire = ToolNaming.Compose("trello", "list_boards");
        wire.Should().Be("trello__list_boards");
        ToolNaming.IsValidWireName(wire).Should().BeTrue();
    }
}
