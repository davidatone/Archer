using System.Collections.Concurrent;
using System.Text.Json;
using Archer.Application.Persistence;

namespace Archer.Persistence;

/// <summary>
/// Default <see cref="IAgentBlobStore"/> implementation: per-process in-memory storage.
/// Tools use this for ephemeral state (todos, scratchpads). The durable audit trail lives
/// in the agent's <c>events.ndjson</c> log — every <c>ToolCallCompletedEvent</c> records
/// what was written, so replaying events would reconstruct the blob state.
///
/// For a multi-process or restart-resilient setup, swap this in DI for a file- or
/// database-backed implementation.
/// </summary>
public sealed class InMemoryAgentBlobStore : IAgentBlobStore
{
    private readonly ConcurrentDictionary<(string AgentId, string Blob), string> _store
        = new();

    public Task<T?> LoadAsync<T>(string agentId, string blobName, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(blobName);
        if (_store.TryGetValue((agentId, blobName), out var json))
        {
            return Task.FromResult(JsonSerializer.Deserialize<T>(json, JsonOptions.Default));
        }
        return Task.FromResult<T?>(null);
    }

    public Task SaveAsync<T>(string agentId, string blobName, T data, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(blobName);
        ArgumentNullException.ThrowIfNull(data);
        var json = JsonSerializer.Serialize(data, JsonOptions.Default);
        _store[(agentId, blobName)] = json;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, string blobName, CancellationToken cancellationToken = default)
    {
        _store.TryRemove((agentId, blobName), out _);
        return Task.CompletedTask;
    }
}
