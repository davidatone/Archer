using System.Text.Json.Nodes;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Model;
using Archer.Domain.Time;
using Archer.Domain.Tools;
using FluentAssertions;

namespace Archer.Persistence.Tests;

/// <summary>
/// Surface-level tests for Domain record types. They mostly exercise constructors,
/// withers, equality, and a few small helpers — cheap line coverage for value types
/// that the rest of the system depends on.
/// </summary>
public class DomainRecordsTests
{
    private const string AgentId = "agent_TESTAGENTXXX1";
    private static readonly DateTimeOffset T = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void AgentMessage_round_trips_required_fields()
    {
        var m = new AgentMessage
        {
            Seq = 7,
            Role = MessageRole.User,
            Content = "hi",
            CreatedAtUtc = T,
            ToolCallId = "tc",
            ToolName = "todo_list",
        };
        m.Seq.Should().Be(7);
        m.Role.Should().Be(MessageRole.User);
        m.ToolCallId.Should().Be("tc");
        m.ToolName.Should().Be("todo_list");

        var copy = m with { Content = "hello" };
        copy.Content.Should().Be("hello");
        copy.Should().NotBe(m);
    }

    [Fact]
    public void AgentSnapshot_From_state_copies_messages_and_summaries()
    {
        var state = new AgentState
        {
            AgentId = AgentId,
            RepoRoot = "/repo",
            CreatedAtUtc = T,
            UpdatedAtUtc = T,
            AgentDefinitionId = "code-scout",
        };
        state.Messages.Add(new AgentMessage
        {
            Seq = 1, Role = MessageRole.User, Content = "hi", CreatedAtUtc = T,
        });
        state.Summaries.Add(new Summary { TurnId = Guid.NewGuid(), Content = "s", CreatedAtUtc = T });

        var snap = AgentSnapshot.From(state);

        snap.AgentId.Should().Be(AgentId);
        snap.Messages.Should().HaveCount(1);
        snap.Summaries.Should().HaveCount(1);
    }

    [Fact]
    public void AgentState_IsTurnActive_matches_on_id_and_seq()
    {
        var t = Guid.NewGuid();
        var state = new AgentState
        {
            AgentId = AgentId,
            RepoRoot = "/r",
            CreatedAtUtc = T,
            AgentDefinitionId = "x",
            ActiveTurnId = t,
            ActiveTurnStartedAtSeq = 5,
        };
        state.IsTurnActive(t, 5).Should().BeTrue();
        state.IsTurnActive(t, 6).Should().BeFalse();
        state.IsTurnActive(Guid.NewGuid(), 5).Should().BeFalse();
    }

    [Fact]
    public void Request_records_construct_with_defaults_and_overrides()
    {
        var req = new NewAgentRequest("/r", "first prompt");
        req.AgentDefinitionId.Should().Be("code-scout");
        req.ModelDeployment.Should().BeNull();

        new UserMessageInput("hi").Content.Should().Be("hi");
        var ack = new UserMessageAccepted(MessageSeq: 3, NewTurnId: Guid.NewGuid(), SupersededTurnId: null);
        ack.MessageSeq.Should().Be(3);

        new InterruptRequest("user").Reason.Should().Be("user");
        new AssistantMessage("done").Text.Should().Be("done");
        var run = new TurnRunRequest(AgentId, Guid.NewGuid(), 3);
        run.AgentId.Should().Be(AgentId);
    }

    [Fact]
    public void Summary_and_TurnIdentity_construct()
    {
        var id = new TurnIdentity(Guid.NewGuid(), 5);
        id.StartedAtMessageSeq.Should().Be(5);

        var s = new Summary { TurnId = Guid.NewGuid(), Content = "abc", CreatedAtUtc = T };
        s.Content.Should().Be("abc");
    }

    [Fact]
    public void Events_set_required_fields_and_kind()
    {
        var common = new
        {
            AgentId,
            TurnId = Guid.NewGuid(),
            CreatedAtUtc = T,
        };

        AgentEvent[] events =
        [
            new TurnStartedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, MessageSeq = 1 },
            new ModelStartedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, Model = "gpt-x" },
            new ToolCallStartedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, ToolCallId = "c", ToolName = "n", Arguments = [] },
            new ToolCallCompletedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, ToolCallId = "c", ToolName = "n", Duration = TimeSpan.FromMilliseconds(1), ResultItemCount = 0, Summary = "s", Success = true },
            new ReasoningEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, Text = "t" },
            new FinalAnswerEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, Text = "t" },
            new TurnSupersededEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, Reason = "r" },
            new TurnCompletedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc },
            new TurnFailedEvent { AgentId = common.AgentId, TurnId = common.TurnId, CreatedAtUtc = common.CreatedAtUtc, Error = "boom" },
        ];

        events.Select(e => e.Kind).Should().NotContainNulls()
            .And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void ModelTurnUpdate_records_construct()
    {
        IModelTurnUpdate[] updates =
        [
            new ModelStartedUpdate("gpt-x"),
            new ModelTextDeltaUpdate("d"),
            new ModelReasoningUpdate("r"),
            new ModelToolCallUpdate(new ModelToolCall("id", "n", [])),
            new ModelFinalAnswerUpdate("f"),
            new ModelErrorUpdate("err"),
        ];
        updates.Should().HaveCount(6);
        updates.OfType<ModelStartedUpdate>().Single().Model.Should().Be("gpt-x");
        updates.OfType<ModelToolCallUpdate>().Single().ToolCall.ToolName.Should().Be("n");
    }

    [Fact]
    public void ModelTurnInput_constructs_with_optional_reasoning()
    {
        var input = new ModelTurnInput(
            AgentId,
            Guid.NewGuid(),
            "sys",
            Messages: [],
            Tools: [],
            ModelDeployment: "gpt-x",
            MaxCompletionTokens: 1000,
            ReasoningEffort: ReasoningEffort.Medium,
            ReasoningSummary: ReasoningSummary.Auto);

        input.MaxCompletionTokens.Should().Be(1000);
        input.ReasoningEffort.Should().Be(ReasoningEffort.Medium);
        input.ReasoningSummary.Should().Be(ReasoningSummary.Auto);
    }

    [Fact]
    public void TodoItem_NewId_creates_unique_ids()
    {
        var a = TodoItem.NewId();
        var b = TodoItem.NewId();
        a.Should().NotBe(b);
        a.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContextProfile_defaults_pin_first_and_use_30_percent()
    {
        var ctx = new ContextProfile();
        ctx.PinFirstMessage.Should().BeTrue();
        ctx.RecentMessageWindow.Fraction.Should().BeApproximately(0.30, 1e-6);
    }

    [Theory]
    [InlineData("30%", 0.30)]
    [InlineData("0.5", 0.50)]
    [InlineData("60", 0.60)]
    public void Percentage_Parse_handles_percent_decimal_and_whole(string input, double expected)
    {
        Percentage.Parse(input).Fraction.Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void Percentage_RoundUp_returns_ceiling()
    {
        Percentage.Of(33).RoundUp(100).Should().Be(33);
        Percentage.Of(33).RoundUp(7).Should().Be(3);
    }

    [Fact]
    public void Percentage_ToString_is_round_trip_safe()
    {
        var p = Percentage.Of(42.5);
        p.ToString().Should().Contain("42.5");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentage_Of_throws_for_out_of_range(double value)
    {
        Action a = () => Percentage.Of(value);
        a.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SystemClock_returns_utc()
    {
        var clock = new SystemClock();
        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ToolResult_Failed_helper_constructs_failure_with_error_summary()
    {
        var r = ToolResult.Failed("c1", "name", "boom", TimeSpan.FromSeconds(1));
        r.Success.Should().BeFalse();
        r.Summary.Should().Be("boom");
        r.ToolCallId.Should().Be("c1");
        r.ToolName.Should().Be("name");
    }
}
