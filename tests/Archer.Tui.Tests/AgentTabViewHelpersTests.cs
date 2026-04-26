using Archer.Domain.Agents;
using Archer.Tui.Ui;
using FluentAssertions;

namespace Archer.Tui.Tests;

/// <summary>
/// Cross-cutting tests for the pure helpers extracted from <see cref="AgentTabView"/>.
/// The helpers themselves live in <see cref="TextRenderer"/> and <see cref="EventRenderer"/>;
/// dedicated tests for those classes cover most of the surface area, but these tests pin
/// down the exact glue used by the view (label-for-role mapping, reasoning header/body
/// formatting) so a future refactor of the view doesn't silently change how chat reads.
/// </summary>
public class AgentTabViewHelpersTests
{
    [Theory]
    [InlineData(MessageRole.User, "you")]
    [InlineData(MessageRole.Assistant, "agent")]
    [InlineData(MessageRole.System, "system")]
    [InlineData(MessageRole.Tool, "tool")]
    public void LabelForRole_maps_known_roles(MessageRole role, string expected)
    {
        TextRenderer.LabelForRole(role).Should().Be(expected);
    }

    [Fact]
    public void FormatReasoning_extracts_bold_header_and_indents_body()
    {
        var formatted = TextRenderer.FormatReasoning("**Header**\n\nbody line one\nbody line two");
        formatted.Should().Contain("┄ thinking ┄");
        formatted.Should().Contain("▸ Header");
        formatted.Should().Contain("  body line one");
        formatted.Should().Contain("  body line two");
    }

    [Fact]
    public void FormatReasoning_falls_back_to_indented_block_without_header()
    {
        var formatted = TextRenderer.FormatReasoning("plain reasoning here\nsecond line");
        formatted.Should().Contain("┄ thinking ┄");
        formatted.Should().Contain("  plain reasoning here");
        formatted.Should().Contain("  second line");
    }

    [Fact]
    public void FormatReasoning_handles_unterminated_bold_marker()
    {
        var formatted = TextRenderer.FormatReasoning("**half-open\nrest");
        // Falls through to the generic indent path because there's no closing **.
        formatted.Should().Contain("**half-open");
    }
}
