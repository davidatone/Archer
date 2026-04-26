using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Model;
using Archer.Domain.Tools;
using Archer.Model.AgentFramework;
using FluentAssertions;

namespace Archer.Model.Tests;

/// <summary>
/// Covers the recent-message-window selection in <see cref="AgentContextBuilder"/> — the
/// percentage-of-context-window calculation, the pin-first-message behaviour, and the
/// system-instructions composition. The tool-filter semantics are exercised separately by
/// <c>AgentContextBuilderFilterTests</c> in the Tools test project.
/// </summary>
public class AgentContextBuilderRecentMessagesTests
{
    private static AgentMessage Msg(long seq, string content, MessageRole role = MessageRole.User) =>
        new()
        {
            Seq = seq,
            Role = role,
            Content = content,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static AgentDefinition Def(int? contextWindowTokens = null, int recentPercent = 30, bool pinFirst = true) =>
        new()
        {
            Id = "test",
            Description = "",
            Instructions = "system instructions",
            Tools = [],
            Context = new ContextProfile
            {
                RecentMessageWindow = Percentage.Of(recentPercent),
                PinFirstMessage = pinFirst,
            },
            Model = new ModelProfile
            {
                Deployment = "gpt-x",
                ContextWindowTokens = contextWindowTokens,
            },
        };

    private static AgentState State(params AgentMessage[] msgs)
    {
        var s = new AgentState
        {
            AgentId = "agent_TESTAGENTXXX1",
            RepoRoot = "/r",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AgentDefinitionId = "test",
            LatestMessageSeq = msgs.Length,
        };
        foreach (var m in msgs) s.Messages.Add(m);
        return s;
    }

    private static AgentContextBuilder NewBuilder() =>
        new(new EmptyToolRegistry());

    [Fact]
    public void Build_throws_for_null_state()
    {
        Action a = () => NewBuilder().Build(null!, Guid.NewGuid(), Def());
        a.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_throws_for_null_definition()
    {
        Action a = () => NewBuilder().Build(State(), Guid.NewGuid(), null!);
        a.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_with_no_messages_returns_empty_message_list()
    {
        var input = NewBuilder().Build(State(), Guid.NewGuid(), Def());
        input.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Build_emits_system_instructions_with_repo_root()
    {
        var input = NewBuilder().Build(State(), Guid.NewGuid(), Def());
        input.SystemInstructions.Should().Contain("system instructions");
        input.SystemInstructions.Should().Contain("Repository root: /r");
    }

    [Fact]
    public void Build_includes_latest_summary_when_present()
    {
        var s = State();
        s.Summaries.Add(new Summary
        {
            TurnId = Guid.NewGuid(),
            Content = "earlier summary",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var input = NewBuilder().Build(s, Guid.NewGuid(), Def());
        input.SystemInstructions.Should().Contain("Latest summary: earlier summary");
    }

    [Fact]
    public void Build_pins_first_message_when_window_excludes_it()
    {
        // Use a tiny window so only the newest message would fit — the pin then forces
        // the first message in too.
        var msgs = new[]
        {
            Msg(1, new string('x', 4000)),
            Msg(2, new string('y', 4000)),
            Msg(3, new string('z', 4000)),
        };
        var input = NewBuilder().Build(State(msgs), Guid.NewGuid(),
            Def(contextWindowTokens: 100, recentPercent: 1, pinFirst: true));
        input.Messages.Should().Contain(m => m.Seq == 1);
    }

    [Fact]
    public void Build_does_not_pin_first_when_PinFirstMessage_is_false()
    {
        var msgs = new[]
        {
            Msg(1, new string('x', 4000)),
            Msg(2, "z"),
        };
        var input = NewBuilder().Build(State(msgs), Guid.NewGuid(),
            Def(contextWindowTokens: 100, recentPercent: 1, pinFirst: false));
        input.Messages.Should().NotContain(m => m.Seq == 1);
    }

    [Fact]
    public void Build_uses_state_model_deployment_when_set()
    {
        var s = State();
        s.ModelDeployment = "gpt-state";
        var input = NewBuilder().Build(s, Guid.NewGuid(), Def());
        input.ModelDeployment.Should().Be("gpt-state");
    }

    [Fact]
    public void Build_falls_back_to_definition_model_deployment_when_state_unset()
    {
        var input = NewBuilder().Build(State(), Guid.NewGuid(), Def());
        input.ModelDeployment.Should().Be("gpt-x");
    }

    [Fact]
    public void Build_uses_state_max_completion_tokens_when_set()
    {
        var s = State();
        s.MaxCompletionTokens = 4096;
        var input = NewBuilder().Build(s, Guid.NewGuid(), Def());
        input.MaxCompletionTokens.Should().Be(4096);
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public bool TryGet(string name, out ITool tool) { tool = null!; return false; }
        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public void Add(ITool tool) { }
        public bool Remove(string name) => false;
        public event Action<ToolRegistryChange>? Changed { add { } remove { } }
    }
}
