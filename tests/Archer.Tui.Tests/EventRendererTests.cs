using System.Text.Json.Nodes;
using Archer.Domain.Events;
using Archer.Tui.Ui;
using FluentAssertions;

namespace Archer.Tui.Tests;

public class EventRendererTests
{
    private const string AgentId = "agent_TESTAGENTXXX1";
    private static readonly Guid Turn = Guid.NewGuid();
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow;

    [Fact]
    public void TurnStarted_appends_to_events_and_clears_reasoning()
    {
        var instr = EventRenderer.Render(new TurnStartedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, MessageSeq = 7,
        });
        instr.AppendToEvents.Should().Be("⏵ turn 0007");
        instr.AppendToChat.Should().BeNull();
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Clear);
    }

    [Fact]
    public void ModelStarted_appends_to_events_only()
    {
        var instr = EventRenderer.Render(new ModelStartedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Model = "gpt-x",
        });
        instr.AppendToEvents.Should().Be("✦ gpt-x");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.None);
    }

    [Fact]
    public void ToolCallStarted_appends_to_events_with_tool_name_and_compact_args()
    {
        var instr = EventRenderer.Render(new ToolCallStartedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T,
            ToolCallId = "c1", ToolName = "search_pattern",
            Arguments = new JsonObject { ["q"] = "needle" },
        });
        instr.AppendToEvents.Should().Contain("search_pattern").And.Contain("needle");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.None);
    }

    [Fact]
    public void ToolCallCompleted_success_uses_summary()
    {
        var instr = EventRenderer.Render(new ToolCallCompletedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T,
            ToolCallId = "c1", ToolName = "search_pattern",
            Duration = TimeSpan.FromMilliseconds(1), ResultItemCount = 1,
            Summary = "found 3", Success = true,
        });
        instr.AppendToEvents.Should().Be("   ↳ found 3");
    }

    [Fact]
    public void ToolCallCompleted_failure_uses_error_with_warning_glyph()
    {
        var instr = EventRenderer.Render(new ToolCallCompletedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T,
            ToolCallId = "c1", ToolName = "search_pattern",
            Duration = TimeSpan.FromMilliseconds(1), ResultItemCount = 0,
            Summary = "boom", Success = false, Error = "exploded",
        });
        instr.AppendToEvents.Should().Contain("⚠").And.Contain("exploded");
    }

    [Fact]
    public void Reasoning_with_text_sets_live_reasoning()
    {
        var instr = EventRenderer.Render(new ReasoningEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Text = "thinking",
        });
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Set);
        instr.SetReasoning.Should().Be("thinking");
    }

    [Fact]
    public void Reasoning_with_blank_text_is_a_noop()
    {
        var instr = EventRenderer.Render(new ReasoningEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Text = "   ",
        });
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.None);
        instr.AppendToEvents.Should().BeNull();
    }

    [Fact]
    public void Summary_with_message_appends_to_events()
    {
        var instr = EventRenderer.Render(new SummaryEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Message = "summary",
        });
        instr.AppendToEvents.Should().Be("≡ summary");
    }

    [Fact]
    public void Summary_with_blank_message_is_a_noop()
    {
        var instr = EventRenderer.Render(new SummaryEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Message = "  ",
        });
        instr.AppendToEvents.Should().BeNull();
    }

    [Fact]
    public void TurnSuperseded_appends_to_events_and_clears_reasoning()
    {
        var instr = EventRenderer.Render(new TurnSupersededEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Reason = "user",
        });
        instr.AppendToEvents.Should().Contain("superseded").And.Contain("user");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Clear);
    }

    [Fact]
    public void FinalAnswer_appends_to_chat_and_clears_reasoning()
    {
        var instr = EventRenderer.Render(new FinalAnswerEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Text = "the answer",
        });
        instr.AppendToChat.Should().Contain("agent>").And.Contain("the answer");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Clear);
    }

    [Fact]
    public void TurnCompleted_appends_to_events_and_clears_reasoning()
    {
        var instr = EventRenderer.Render(new TurnCompletedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T,
        });
        instr.AppendToEvents.Should().Be("⏹ turn complete");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Clear);
    }

    [Fact]
    public void TurnFailed_appends_to_chat_and_events_and_clears_reasoning()
    {
        var instr = EventRenderer.Render(new TurnFailedEvent
        {
            AgentId = AgentId, TurnId = Turn, CreatedAtUtc = T, Error = "kaboom",
        });
        instr.AppendToChat.Should().Contain("[error]").And.Contain("kaboom");
        instr.AppendToEvents.Should().Contain("⚠").And.Contain("kaboom");
        instr.Reasoning.Should().Be(EventRenderer.ReasoningOp.Clear);
    }

    [Fact]
    public void Compact_returns_short_strings_unchanged()
    {
        EventRenderer.Compact("hello").Should().Be("hello");
    }

    [Fact]
    public void Compact_truncates_at_90_chars_with_ellipsis()
    {
        var s = new string('x', 200);
        var compacted = EventRenderer.Compact(s);
        compacted.Length.Should().Be(90);
        compacted.Should().EndWith("...");
    }
}
