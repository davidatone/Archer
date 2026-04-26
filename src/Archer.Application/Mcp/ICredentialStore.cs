using Archer.Domain.Mcp;

namespace Archer.Application.Mcp;

/// <summary>
/// Encrypted storage for MCP server credentials. Each server's credentials are keyed
/// by its name and persisted in a single encrypted blob file. Implementations must
/// guarantee that on-disk content is not readable as plaintext without the key.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Load credentials for a server, or null if none have been stored.</summary>
    Task<ServerCredentials?> GetAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>Atomically replace any existing credentials for the named server.</summary>
    Task SaveAsync(string serverName, ServerCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Remove credentials for a server. Idempotent — returns true if anything was removed.</summary>
    Task<bool> DeleteAsync(string serverName, CancellationToken cancellationToken = default);
}
