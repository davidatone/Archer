using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using ModelContextProtocol.Authentication;

namespace Archer.Mcp.Auth;

/// <summary>
/// Adapts <see cref="ICredentialStore"/> to the MCP SDK's <see cref="ITokenCache"/> contract.
/// One instance per (server). The SDK calls <c>GetTokensAsync</c> on each connect and
/// <c>StoreTokensAsync</c> after the OAuth dance / refresh; we round-trip those onto the
/// encrypted credential blob keyed by server name.
/// </summary>
public sealed class CredentialStoreTokenCache : ITokenCache
{
    private readonly ICredentialStore _store;
    private readonly string _serverName;

    public CredentialStoreTokenCache(ICredentialStore store, string serverName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverName);
        _store = store;
        _serverName = serverName;
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var creds = await _store.GetAsync(_serverName, cancellationToken).ConfigureAwait(false);
        if (creds?.OAuth is not { AccessToken.Length: > 0 } oauth)
        {
            return null;
        }

        return new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = oauth.AccessToken,
            RefreshToken = oauth.RefreshToken,
            ExpiresIn = oauth.ExpiresAtUtc is { } e
                ? (int)Math.Max(0, (e - creds.SavedAtUtc).TotalSeconds)
                : null,
            Scope = oauth.Scopes.Count > 0 ? string.Join(' ', oauth.Scopes) : null,
            ObtainedAt = creds.SavedAtUtc,
        };
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var existing = await _store.GetAsync(_serverName, cancellationToken).ConfigureAwait(false)
            ?? new ServerCredentials();

        var expiresAt = tokens.ExpiresIn is { } secs && secs > 0
            ? tokens.ObtainedAt.AddSeconds(secs)
            : (DateTimeOffset?)null;

        var scopes = string.IsNullOrWhiteSpace(tokens.Scope)
            ? Array.Empty<string>()
            : tokens.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var existingOAuth = existing.OAuth;
        var updated = existing with
        {
            OAuth = new OAuthTokens
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken ?? existingOAuth?.RefreshToken,
                ExpiresAtUtc = expiresAt,
                Scopes = scopes,
                Issuer = existingOAuth?.Issuer,
                ClientId = existingOAuth?.ClientId,
            },
        };

        await _store.SaveAsync(_serverName, updated, cancellationToken).ConfigureAwait(false);
    }
}
