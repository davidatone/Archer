using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Archer.Events;

/// <summary>
/// Decorator that wraps an inner <see cref="IAgentEventSink"/> and additionally appends each
/// published event to an <see cref="IAgentStateStore"/>'s NDJSON event log. Persistence
/// failures are logged and swallowed so they never break the in-memory event stream.
/// </summary>
public sealed class PersistingAgentEventSink : IAgentEventSink
{
    private readonly IAgentEventSink _inner;
    private readonly IAgentStateStore _store;
    private readonly ILogger<PersistingAgentEventSink> _logger;

    public PersistingAgentEventSink(
        IAgentEventSink inner,
        IAgentStateStore store,
        ILogger<PersistingAgentEventSink> logger)
    {
        _inner = inner;
        _store = store;
        _logger = logger;
    }

    public async ValueTask PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        await _inner.PublishAsync(evt, cancellationToken);
        try
        {
            await _store.AppendEventAsync(evt.AgentId, evt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist event for {AgentId}", evt.AgentId);
        }
    }

    public IAsyncEnumerable<AgentEvent> SubscribeAsync(string agentId, CancellationToken cancellationToken = default)
        => _inner.SubscribeAsync(agentId, cancellationToken);
}
