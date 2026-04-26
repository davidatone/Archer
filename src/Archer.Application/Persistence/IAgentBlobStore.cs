namespace Archer.Application.Persistence;

/// <summary>
/// Per-agent JSON blob storage for tool-owned state. Tools that need to remember anything
/// across calls (todos, notes, scratchpads, …) read and write through this interface
/// rather than reaching into the agent's domain state. Each blob is keyed by
/// <c>(agentId, blobName)</c> and is independent of <see cref="IAgentStateStore"/>'s
/// canonical agent state.
/// </summary>
public interface IAgentBlobStore
{
    /// <summary>Load a previously stored blob, or null if it doesn't exist.</summary>
    Task<T?> LoadAsync<T>(string agentId, string blobName, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>Atomically write a blob (tmp file + rename).</summary>
    Task SaveAsync<T>(string agentId, string blobName, T data, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>Delete a blob if it exists; idempotent.</summary>
    Task DeleteAsync(string agentId, string blobName, CancellationToken cancellationToken = default);
}
