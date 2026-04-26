namespace Archer.Domain.Mcp;

/// <summary>
/// Credentials for one MCP server. Stored encrypted, keyed by server name.
/// Only one of <see cref="BearerToken"/>, <see cref="ApiKey"/>, <see cref="OAuth"/>
/// is meaningful for a given server (matched against <see cref="McpAuthConfig.Type"/>).
/// </summary>
public sealed record ServerCredentials
{
    public string? BearerToken { get; init; }

    public ApiKeyPair? ApiKey { get; init; }

    public OAuthTokens? OAuth { get; init; }

    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Two-part key+token style used by services like Trello. The exact wire encoding
/// (query params vs headers) is the MCP server's concern, not Archer's — these are
/// passed to the sidecar via env vars or to the server via the agreed scheme.
/// </summary>
public sealed record ApiKeyPair
{
    public required string Key { get; init; }
    public required string Token { get; init; }
}

public sealed record OAuthTokens
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>The authorization-server issuer URL the token was minted from. Used to
    /// detect tenant/realm mix-ups on refresh.</summary>
    public string? Issuer { get; init; }

    /// <summary>The (possibly DCR-registered) client id used to obtain this token.</summary>
    public string? ClientId { get; init; }
}
