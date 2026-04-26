using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Archer.Actors.Contracts;
using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;
using Archer.Persistence;
using Archer.Tools;
using Archer.Tui.Sessions;
using FluentAssertions;

namespace Archer.Tui.Tests;

public class AgentSessionTests
{
    private const string AgentId = "agent_TESTAGENTXXX1";

    /// <summary>Channel-backed sink so tests can drive events deterministically.</summary>
    private sealed class StubSink : IAgentEventSink
    {
        public Channel<AgentEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();

        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct = default) =>
            Channel.Writer.WriteAsync(evt, ct);

        public async IAsyncEnumerable<AgentEvent> SubscribeAsync(
            string agentId,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in Channel.Reader.ReadAllAsync(ct))
            {
                if (e.AgentId == agentId) yield return e;
            }
        }

        public void Complete() => Channel.Writer.TryComplete();
    }

    /// <summary>Stub grain that returns a canned snapshot on demand. We never call other methods.</summary>
    private sealed class StubGrain : IArcherAgentGrain
    {
        public AgentSnapshot? Snapshot { get; set; }
        public int GetSnapshotCalls { get; private set; }

        public Task<AgentSnapshot> InitializeAsync(NewAgentRequest request) =>
            throw new NotSupportedException();
        public Task<UserMessageAccepted> AddUserMessageAsync(UserMessageInput input) =>
            throw new NotSupportedException();
        public Task InterruptAsync(InterruptRequest request) => Task.CompletedTask;
        public Task<AgentSnapshot?> GetSnapshotAsync()
        {
            GetSnapshotCalls++;
            return Task.FromResult(Snapshot);
        }
        public Task<bool> IsTurnStillActiveAsync(Guid turnId, long messageSeq) => Task.FromResult(true);
        public Task AppendTurnEventAsync(Guid turnId, AgentEvent evt) => Task.CompletedTask;
        public Task RecordToolResultAsync(Guid turnId, int index, ToolResult result) => Task.CompletedTask;
        public Task<bool> CommitFinalAnswerIfStillActiveAsync(Guid turnId, long messageSeq, AssistantMessage final) =>
            Task.FromResult(true);
    }

    private static AgentSnapshot MakeSnapshot(Guid? activeTurn = null) => new(
        AgentId: AgentId,
        RepoRoot: "/r",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        LatestMessageSeq: 1,
        ActiveTurnId: activeTurn,
        ActiveTurnStartedAtSeq: activeTurn is null ? null : 1,
        AgentDefinitionId: "code-scout",
        ModelDeployment: null,
        MaxCompletionTokens: null,
        Messages: [],
        Summaries: []);

    [Fact]
    public async Task RefreshSnapshotAsync_populates_messages_and_invokes_StateChanged()
    {
        var grain = new StubGrain
        {
            Snapshot = MakeSnapshot() with
            {
                Messages =
                [
                    new() { Seq = 1, Role = MessageRole.User, Content = "hi", CreatedAtUtc = DateTimeOffset.UtcNow },
                ],
            },
        };
        var sink = new StubSink();
        using var session = new AgentSession(AgentId, "/r", "code-scout", () => grain, sink);

        var stateChanged = 0;
        session.StateChanged += () => stateChanged++;

        await session.RefreshSnapshotAsync();

        session.Messages.Should().HaveCount(1);
        stateChanged.Should().Be(1);
    }

    [Fact]
    public async Task RefreshSnapshotAsync_returns_null_snapshot_silently()
    {
        var grain = new StubGrain { Snapshot = null };
        var sink = new StubSink();
        using var session = new AgentSession(AgentId, "/r", "code-scout", () => grain, sink);

        Func<Task> act = () => session.RefreshSnapshotAsync();
        await act.Should().NotThrowAsync();
        session.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshSnapshotAsync_pulls_todos_from_blob_store_when_provided()
    {
        var grain = new StubGrain { Snapshot = MakeSnapshot() };
        var blobStore = new InMemoryAgentBlobStore();
        await blobStore.SaveAsync(AgentId, "todos", new TodoListTool.TodoList
        {
            Items =
            [
                new TodoItem
                {
                    Id = TodoItem.NewId(),
                    Title = "x",
                    Status = TodoStatus.Todo,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
        });

        var sink = new StubSink();
        using var session = new AgentSession(AgentId, "/r", "code-scout", () => grain, sink, blobStore);

        await session.RefreshSnapshotAsync();
        session.Todos.Should().HaveCount(1);
    }

    [Fact]
    public async Task Pump_marks_thinking_on_TurnStarted_and_clears_on_TurnCompleted()
    {
        var grain = new StubGrain { Snapshot = MakeSnapshot() };
        var sink = new StubSink();
        using var session = new AgentSession(AgentId, "/r", "code-scout", () => grain, sink);

        var turnId = Guid.NewGuid();
        await sink.PublishAsync(new TurnStartedEvent
        {
            AgentId = AgentId, TurnId = turnId, CreatedAtUtc = DateTimeOffset.UtcNow, MessageSeq = 1,
        });

        // Wait for the pump to drain the event.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !session.IsThinking) await Task.Delay(20);
        session.IsThinking.Should().BeTrue();
        session.ActiveTurnId.Should().Be(turnId);

        await sink.PublishAsync(new TurnCompletedEvent
        {
            AgentId = AgentId, TurnId = turnId, CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && session.IsThinking) await Task.Delay(20);
        session.IsThinking.Should().BeFalse();
        session.ActiveTurnId.Should().BeNull();

        sink.Complete();
    }

    [Fact]
    public void Constructor_overload_accepting_grain_resolver_records_basic_props()
    {
        var grain = new StubGrain();
        var sink = new StubSink();
        using var session = new AgentSession("a-id", "/some/path", "type-X", () => grain, sink);
        session.AgentId.Should().Be("a-id");
        session.RepoRoot.Should().Be("/some/path");
        session.AgentType.Should().Be("type-X");
        session.Grain.Should().BeSameAs(grain);
    }
}
