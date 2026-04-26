using Archer.Domain.Events;

namespace Archer.Application.Events;

public interface IAgentEventSink
{
    ValueTask PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentEvent> SubscribeAsync(string agentId, CancellationToken cancellationToken = default);
}
