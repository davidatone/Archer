using Archer.Domain.Mcp;

namespace Archer.Application.Mcp;

/// <summary>
/// Live, mutable registry of MCP server configurations. Backed by YAML files watched
/// for changes. Supports runtime add/remove via <see cref="AddOrUpdateAsync"/> and
/// <see cref="RemoveAsync"/>, which persist to the user-level configuration directory.
/// </summary>
public interface IMcpServerRegistry
{
    /// <summary>Atomic snapshot of currently-known server configs.</summary>
    IReadOnlyList<McpServerConfig> All { get; }

    McpServerConfig? Get(string name);

    /// <summary>Persist a new or updated server config to user-level storage. Existing
    /// configs with the same name are replaced. The change event fires after the file
    /// is committed and the in-memory snapshot updated.</summary>
    Task AddOrUpdateAsync(McpServerConfig config, CancellationToken cancellationToken = default);

    /// <summary>Remove a server config (and any user-level YAML file backing it).
    /// Repo-level configs cannot be removed via this API — they're considered
    /// project-level configuration. Returns true if anything was removed.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Fired (after the registry mutates) when a server is added, changed, or removed.</summary>
    event Action<McpRegistryChange>? Changed;
}

public sealed record McpRegistryChange(McpRegistryChangeKind Kind, McpServerConfig Server);

public enum McpRegistryChangeKind
{
    Added,
    Updated,
    Removed,
}
