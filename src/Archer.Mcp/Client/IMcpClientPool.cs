using ModelContextProtocol.Client;

namespace Archer.Mcp.Client;

/// <summary>
/// Per-server cache of connected <see cref="McpClient"/>s. Lazy-connects on first request,
/// reuses across calls, single-flight under contention. Auth headers are resolved from the
/// credential store at connect time. Connections survive transient errors via the SDK's
/// built-in reconnect; the pool reconnects once it observes a hard failure.
/// </summary>
public interface IMcpClientPool : IAsyncDisposable
{
    /// <summary>Get a connected client for the named server, connecting if necessary.</summary>
    /// <exception cref="InvalidOperationException">No such server, or it's disabled.</exception>
    ValueTask<McpClient> GetAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>Drop the cached client for a server (e.g. on config change). Idempotent.</summary>
    ValueTask EvictAsync(string serverName, CancellationToken cancellationToken = default);
}
