using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Archer.Application.Events;
using Archer.Domain.Events;

namespace Archer.Events;

/// <summary>
/// In-process pub/sub for <see cref="AgentEvent"/>. One <see cref="Channel{T}"/> per subscriber,
/// keyed by <see cref="AgentEvent.AgentId"/>. Single responsibility — no persistence here; layer
/// <see cref="PersistingAgentEventSink"/> on top for that.
/// </summary>
public sealed class ChannelAgentEventSink : IAgentEventSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentBroadcaster> _broadcasters = new(StringComparer.Ordinal);

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var broadcaster = _broadcasters.GetOrAdd(evt.AgentId, static id => new AgentBroadcaster(id));
        broadcaster.Publish(evt);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AgentEvent> SubscribeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var broadcaster = _broadcasters.GetOrAdd(agentId, static id => new AgentBroadcaster(id));
        return broadcaster.SubscribeAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _broadcasters.Values)
        {
            await b.DisposeAsync();
        }
        _broadcasters.Clear();
    }

    private sealed class AgentBroadcaster : IAsyncDisposable
    {
        private readonly string _agentId;
        private readonly ConcurrentDictionary<Guid, Channel<AgentEvent>> _subscribers = new();

        public AgentBroadcaster(string agentId) => _agentId = agentId;

        public void Publish(AgentEvent evt)
        {
            foreach (var ch in _subscribers.Values)
            {
                ch.Writer.TryWrite(evt);
            }
        }

        public async IAsyncEnumerable<AgentEvent> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            _subscribers[id] = channel;
            try
            {
                while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var evt))
                    {
                        yield return evt;
                    }
                }
            }
            finally
            {
                _subscribers.TryRemove(id, out _);
                channel.Writer.TryComplete();
            }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var ch in _subscribers.Values)
            {
                ch.Writer.TryComplete();
            }
            _subscribers.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
