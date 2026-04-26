using Archer.Domain.Agents;
using Archer.Tui.Ui;
using FluentAssertions;

namespace Archer.Tui.Tests;

public class TextRendererTests
{
    [Fact]
    public void ComposeChat_returns_committed_when_no_live_reasoning()
    {
        TextRenderer.ComposeChat("you> hi\n", string.Empty).Should().Be("you> hi\n");
    }

    [Fact]
    public void ComposeChat_appends_reasoning_with_separator_when_committed_lacks_trailing_newline()
    {
        var result = TextRenderer.ComposeChat("you> hi", "┄ thinking ┄\n  step\n");
        result.Should().StartWith("you> hi" + Environment.NewLine);
        result.Should().Contain("thinking");
    }

    [Fact]
    public void ComposeChat_does_not_double_separator_when_committed_already_ends_newline()
    {
        var result = TextRenderer.ComposeChat("you> hi\n", "┄ thinking ┄\n  step\n");
        result.Should().Be("you> hi\n┄ thinking ┄\n  step\n");
    }

    [Fact]
    public void FormatTranscript_renders_each_message_with_role_label()
    {
        var msgs = new List<AgentMessage>
        {
            new() { Seq = 1, Role = MessageRole.User, Content = "first", CreatedAtUtc = DateTimeOffset.UtcNow },
            new() { Seq = 2, Role = MessageRole.Assistant, Content = "reply", CreatedAtUtc = DateTimeOffset.UtcNow },
        };
        var output = TextRenderer.FormatTranscript(msgs);
        output.Should().Contain("you> first");
        output.Should().Contain("agent> reply");
    }

    [Fact]
    public void FormatTranscript_returns_empty_for_no_messages()
    {
        TextRenderer.FormatTranscript([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TodoStatus.Done, "✓")]
    [InlineData(TodoStatus.Doing, "◐")]
    [InlineData(TodoStatus.Blocked, "✗")]
    [InlineData(TodoStatus.Todo, "☐")]
    public void FormatTodo_uses_correct_glyph_per_status(TodoStatus status, string glyph)
    {
        var todo = new TodoItem
        {
            Id = "1",
            Title = "investigate",
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        TextRenderer.FormatTodo(todo).Should().Be($"{glyph} investigate");
    }

    [Fact]
    public void FormatStatus_with_no_active_turn_shows_idle()
    {
        TextRenderer.FormatStatus("agent_x", null, isThinking: false, messageCount: 5, todoCount: 2)
            .Should().Contain("turn:idle")
            .And.Contain("msgs:5")
            .And.Contain("todos:2");
    }

    [Fact]
    public void FormatStatus_truncates_active_turn_to_first_six_hex_chars()
    {
        var t = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
        TextRenderer.FormatStatus("agent_x", t, isThinking: false, messageCount: 0, todoCount: 0)
            .Should().Contain("turn:123456");
    }

    [Fact]
    public void FormatStatus_includes_thinking_marker_when_active()
    {
        var s = TextRenderer.FormatStatus("agent_x", Guid.NewGuid(), isThinking: true, 0, 0);
        s.Should().Contain("thinking…");
    }
}
