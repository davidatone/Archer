using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;
using Archer.Events;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Events.Tests;

public class EventsServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddArcherEvents_with_no_state_store_returns_channel_sink_directly()
    {
        var services = new ServiceCollection();
        services.AddArcherEvents();
        await using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAgentEventSink>().Should().BeOfType<ChannelAgentEventSink>();
    }

    [Fact]
    public async Task AddArcherEvents_with_state_store_wraps_in_persisting_decorator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddSingleton<IAgentStateStore, NullStateStore>();
        services.AddArcherEvents();
        await using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAgentEventSink>().Should().BeOfType<PersistingAgentEventSink>();
    }

    private sealed class NullStateStore : IAgentStateStore
    {
        public Task<AgentState?> LoadAsync(string id, CancellationToken ct = default) => Task.FromResult<AgentState?>(null);
        public Task SaveAsync(AgentState state, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task AppendEventAsync(string id, AgentEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveToolResultAsync(string id, Guid t, int idx, ToolResult r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
