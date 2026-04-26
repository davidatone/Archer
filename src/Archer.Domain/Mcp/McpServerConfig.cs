namespace Archer.Domain.Mcp;

/// <summary>
/// Declarative configuration for one MCP server. Loaded from <c>mcp/&lt;name&gt;.yaml</c>.
/// Contains connection details only — secrets (bearer tokens, API keys, OAuth tokens)
/// live in the credential store, keyed by <see cref="Name"/>.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Unique server identifier. Lowercase ASCII, dash/underscore allowed.
    /// Used as the prefix for tool names: <c>{Name}.{tool}</c>.</summary>
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool Disabled { get; init; }

    public required McpTransportConfig Transport { get; init; }

    public McpAuthConfig Auth { get; init; } = new() { Type = McpAuthType.None };

    public McpTimeouts Timeouts { get; init; } = new();

    public McpRetryPolicy Retry { get; init; } = new();

    /// <summary>Optional per-tool overrides — does NOT act as a whitelist. Tools the
    /// server exposes but aren't listed here use the server-advertised metadata as-is.</summary>
    public IReadOnlyList<McpToolOverride> ToolOverrides { get; init; } = [];

    /// <summary>Origin path on disk; null for in-memory configs.</summary>
    public string? SourcePath { get; init; }
}

public sealed record McpTransportConfig
{
    public required McpTransportType Type { get; init; }

    /// <summary>For <see cref="McpTransportType.Sse"/> and <see cref="McpTransportType.StreamableHttp"/>.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Static request headers (NOT for credentials — use the credential store).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>For <see cref="McpTransportType.Stdio"/>: the executable to spawn.</summary>
    public string? Command { get; init; }

    /// <summary>For <see cref="McpTransportType.Stdio"/>: command-line arguments.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>For <see cref="McpTransportType.Stdio"/>: environment variables for the spawned process.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum McpTransportType
{
    /// <summary>Server-Sent Events over HTTP. Legacy MCP HTTP transport.</summary>
    Sse,

    /// <summary>2025-06-18 spec Streamable HTTP. Preferred for HTTP servers.</summary>
    StreamableHttp,

    /// <summary>Local subprocess over stdio. Spawned by the host.</summary>
    Stdio,
}

public sealed record McpAuthConfig
{
    public required McpAuthType Type { get; init; }

    /// <summary>For OAuth: optional override of the authorization server discovered from PRM.</summary>
    public Uri? AuthorizationServer { get; init; }

    /// <summary>For OAuth: requested scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>For OAuth: pre-registered client id (else Dynamic Client Registration is attempted).</summary>
    public string? ClientId { get; init; }

    /// <summary>For OAuth: redirect-uri port for the local browser callback. 0 = pick free.</summary>
    public int RedirectPort { get; init; }
}

public enum McpAuthType
{
    None,
    Bearer,
    ApiKey,
    OAuth,
}

public sealed record McpTimeouts
{
    public int ConnectSeconds { get; init; } = 30;
    public int CallSeconds { get; init; } = 60;
}

public sealed record McpRetryPolicy
{
    public int MaxAttempts { get; init; } = 3;
    public int InitialDelayMs { get; init; } = 500;
    public McpRetryBackoff Backoff { get; init; } = McpRetryBackoff.Exponential;
}

public enum McpRetryBackoff
{
    Constant,
    Exponential,
}

public sealed record McpToolOverride
{
    public required string Name { get; init; }
    public string? DescriptionOverride { get; init; }
}
