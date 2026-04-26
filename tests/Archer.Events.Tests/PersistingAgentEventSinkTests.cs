using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;
using Archer.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Events.Tests;

public class PersistingAgentEventSinkTests
{
    [Fact]
    public async Task PublishAsync_publishes_to_inner_then_persists_to_store()
    {
        var inner = new RecordingSink();
        var store = new RecordingStore();
        var decorator = new PersistingAgentEventSink(inner, store, NullLogger<PersistingAgentEventSink>.Instance);
        var evt = new SummaryEvent
        {
            AgentId = "agent_X", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "hello",
        };

        await decorator.PublishAsync(evt);

        inner.Published.Should().HaveCount(1);
        inner.Published[0].Should().BeSameAs(evt);
        store.Appended.Should().HaveCount(1);
        store.Appended[0].agentId.Should().Be("agent_X");
        store.Appended[0].evt.Should().BeSameAs(evt);
    }

    [Fact]
    public async Task PublishAsync_swallows_persistence_failure()
    {
        var inner = new RecordingSink();
        var store = new RecordingStore { ThrowOnAppend = new IOException("disk full") };
        var decorator = new PersistingAgentEventSink(inner, store, NullLogger<PersistingAgentEventSink>.Instance);
        var evt = new SummaryEvent
        {
            AgentId = "agent_X", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "x",
        };

        var act = async () => await decorator.PublishAsync(evt);
        await act.Should().NotThrowAsync();

        // The event still flowed into the in-memory channel — broken disk doesn't break subscribers.
        inner.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_throws_for_null_event()
    {
        var decorator = new PersistingAgentEventSink(new RecordingSink(), new RecordingStore(), NullLogger<PersistingAgentEventSink>.Instance);
        var act = async () => await decorator.PublishAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void SubscribeAsync_delegates_to_inner_sink()
    {
        var inner = new RecordingSink();
        var decorator = new PersistingAgentEventSink(inner, new RecordingStore(), NullLogger<PersistingAgentEventSink>.Instance);

        var stream = decorator.SubscribeAsync("agent_X");

        stream.Should().BeSameAs(inner.LastSubscribeStream);
        inner.LastSubscribeAgentId.Should().Be("agent_X");
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        public List<AgentEvent> Published { get; } = [];
        public string? LastSubscribeAgentId { get; private set; }
        public IAsyncEnumerable<AgentEvent>? LastSubscribeStream { get; private set; }

        public ValueTask PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default)
        {
            Published.Add(evt);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<AgentEvent> SubscribeAsync(string agentId, CancellationToken cancellationToken = default)
        {
            LastSubscribeAgentId = agentId;
            LastSubscribeStream = AsyncEnumerable.Empty<AgentEvent>();
            return LastSubscribeStream;
        }
    }

    private sealed class RecordingStore : IAgentStateStore
    {
        public List<(string agentId, AgentEvent evt)> Appended { get; } = [];
        public Exception? ThrowOnAppend { get; set; }

        public Task AppendEventAsync(string agentId, AgentEvent evt, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAppend is not null) throw ThrowOnAppend;
            Appended.Add((agentId, evt));
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<AgentState?> LoadAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult<AgentState?>(null);
        public Task SaveAsync(AgentState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveToolResultAsync(string agentId, Guid turnId, int index, ToolResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListAgentsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => new EmptyAsyncEnumerable<T>();
        private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>
        {
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => new Enumerator();
            private sealed class Enumerator : IAsyncEnumerator<T>
            {
                public T Current => default!;
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
                public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
            }
        }
    }
}
