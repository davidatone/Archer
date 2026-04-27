namespace Archer.Application.Mcp;

/// <summary>
/// Lifecycle state of an MCP server connection from the host's point of view. Surfaced by
/// <c>McpToolSource</c> so the TUI/CLI can render a status indicator without each consumer
/// having to probe the pool itself.
/// </summary>
public enum McpConnectionState
{
    /// <summary>Host hasn't tried to connect yet (e.g. just launched).</summary>
    NotAttempted,

    /// <summary>Auth type requires credentials and none are stored. Eager-connect skipped.</summary>
    NeedsCredentials,

    /// <summary>Connect / tool-enumeration is in progress.</summary>
    Connecting,

    /// <summary>Connected and tools are registered.</summary>
    Connected,

    /// <summary>Last attempt failed; <see cref="McpServerStatus.Error"/> carries the message.</summary>
    Failed,

    /// <summary>Server config exists but is marked <c>disabled: true</c>.</summary>
    Disabled,
}

/// <summary>
/// Snapshot of an MCP server's host-side connection state. Equality is by name + state +
/// error + tool count so consumers can use <c>HashSet</c> semantics for "did this change?"
/// </summary>
public sealed record McpServerStatus
{
    public required string ServerName { get; init; }
    public required McpConnectionState State { get; init; }

    /// <summary>Number of tools registered for this server. Zero when not connected.</summary>
    public required int ToolCount { get; init; }

    /// <summary>One-line failure description when <see cref="State"/> is <see cref="McpConnectionState.Failed"/>.</summary>
    public string? Error { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
