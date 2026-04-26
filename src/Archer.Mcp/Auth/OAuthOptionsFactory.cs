using System.Net;
using System.Net.Sockets;
using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace Archer.Mcp.Auth;

/// <summary>
/// Composes a <see cref="ClientOAuthOptions"/> from an <see cref="McpServerConfig"/>,
/// the encrypted credential store, and the browser-based redirect flow.
/// </summary>
public static class OAuthOptionsFactory
{
    public static ClientOAuthOptions Build(
        McpServerConfig server,
        ICredentialStore credentials,
        ServerCredentials? cachedCredentials,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(credentials);

        var port = server.Auth.RedirectPort;
        if (port == 0)
        {
            port = FindFreeLocalPort();
        }
        var redirectUri = new Uri($"http://localhost:{port}/callback/");

        var flow = new BrowserAuthFlow(logger);

        // Effective client id: explicit YAML > previously-DCR-registered (cached) > none (DCR will run).
        var effectiveClientId = server.Auth.ClientId
            ?? cachedCredentials?.OAuth?.ClientId;

        var options = new ClientOAuthOptions
        {
            ClientId = effectiveClientId,
            RedirectUri = redirectUri,
            Scopes = server.Auth.Scopes.Count > 0 ? server.Auth.Scopes : null,
            TokenCache = new CredentialStoreTokenCache(credentials, server.Name),
            AuthorizationRedirectDelegate = flow.HandleAsync,
        };

        // DCR runs only when there's no client id from any source. When DCR succeeds,
        // persist the assigned client_id so the next connect doesn't re-register.
        if (string.IsNullOrEmpty(effectiveClientId))
        {
            options.DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "Archer",
                ResponseDelegate = (resp, ct) => PersistDcrClientIdAsync(credentials, server.Name, resp, logger, ct),
            };
        }

        // Issuer pinning: if the YAML names a specific authorization server, refuse to
        // use any other one the PRM might list. Defends against rogue PRMs and multi-tenant
        // realm mix-ups. When unset, take the PRM's first listed server.
        if (server.Auth.AuthorizationServer is { } pinned)
        {
            options.AuthServerSelector = candidates => SelectPinned(candidates, pinned, server.Name, logger);
        }

        return options;
    }

    private static Uri SelectPinned(IReadOnlyList<Uri> candidates, Uri pinned, string serverName, ILogger? logger)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (UriEquals(candidates[i], pinned))
            {
                return candidates[i];
            }
        }
        var listed = string.Join(", ", candidates.Select(c => c.ToString()));
        logger?.LogError(
            "MCP server '{Server}': configured authorizationServer {Pinned} is not in the PRM-listed " +
            "authorization servers ({Listed}). Refusing to authenticate.",
            serverName, pinned, listed);
        throw new InvalidOperationException(
            $"Configured authorizationServer for '{serverName}' ({pinned}) is not in the protected " +
            $"resource metadata's authorization_servers list ({listed}).");
    }

    private static bool UriEquals(Uri a, Uri b)
    {
        // Compare normalized; tolerate trailing slash differences.
        var aStr = a.ToString().TrimEnd('/');
        var bStr = b.ToString().TrimEnd('/');
        return string.Equals(aStr, bStr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Capture the client_id (and client_secret, if any) handed back by Dynamic Client
    /// Registration so subsequent connects can reuse it instead of re-registering. Merged
    /// onto whatever <see cref="ServerCredentials.OAuth"/> already exists.
    /// </summary>
    private static async Task PersistDcrClientIdAsync(
        ICredentialStore store,
        string serverName,
        DynamicClientRegistrationResponse response,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await store.GetAsync(serverName, cancellationToken).ConfigureAwait(false)
                ?? new ServerCredentials();
            var existingOAuth = existing.OAuth ?? new OAuthTokens { AccessToken = string.Empty };
            var updated = existing with
            {
                OAuth = existingOAuth with { ClientId = response.ClientId },
            };
            await store.SaveAsync(serverName, updated, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation(
                "Persisted DCR client_id for MCP server '{Server}'.", serverName);
        }
        catch (Exception ex)
        {
            // Don't fail the OAuth flow over a cache write — the user can still log in this
            // session; we just won't have the speedup on subsequent connects.
            logger?.LogWarning(ex,
                "Could not persist DCR client_id for '{Server}'. Future connects will re-register.",
                serverName);
        }
    }

    private static int FindFreeLocalPort()
    {
        // Bind to 0 → OS picks an ephemeral port → release immediately. There's a small
        // race window before the auth listener binds it, but localhost ephemeral ports
        // turn over slowly enough that this is reliable in practice.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
