using Archer.Application.Events;
using Archer.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archer.Events;

public static class EventsServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-process Channel-based sink. If <see cref="IAgentStateStore"/> is also
    /// registered (it is by default in the host), the sink is wrapped with
    /// <see cref="PersistingAgentEventSink"/> so events also land in the agent's NDJSON log.
    /// </summary>
    public static IServiceCollection AddArcherEvents(this IServiceCollection services)
    {
        services.AddSingleton<ChannelAgentEventSink>();
        services.AddSingleton<IAgentEventSink>(sp =>
        {
            var inner = sp.GetRequiredService<ChannelAgentEventSink>();
            var store = sp.GetService<IAgentStateStore>();
            if (store is null)
            {
                return inner;
            }
            var logger = sp.GetRequiredService<ILogger<PersistingAgentEventSink>>();
            return new PersistingAgentEventSink(inner, store, logger);
        });
        return services;
    }
}
