using Archer.Domain.Mcp;
using Archer.Mcp.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;

namespace Archer.Mcp.Tests;

public sealed class CredentialStoreTokenCacheTests : IDisposable
{
    private readonly string _root;
    private readonly Credentials.EncryptedFileCredentialStore _store;

    public CredentialStoreTokenCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-tokencache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var dp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        _store = new Credentials.EncryptedFileCredentialStore(
            Path.Combine(_root, "credentials.dat"), dp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetTokensAsync_returns_null_when_no_credentials_stored()
    {
        var cache = new CredentialStoreTokenCache(_store, "github");

        var tokens = await cache.GetTokensAsync(CancellationToken.None);

        tokens.Should().BeNull();
    }

    [Fact]
    public async Task GetTokensAsync_returns_null_when_credentials_have_no_oauth()
    {
        await _store.SaveAsync("github", new ServerCredentials { BearerToken = "x" });
        var cache = new CredentialStoreTokenCache(_store, "github");

        var tokens = await cache.GetTokensAsync(CancellationToken.None);

        tokens.Should().BeNull();
    }

    [Fact]
    public async Task StoreTokensAsync_persists_oauth_section_with_expiry_and_scopes()
    {
        var cache = new CredentialStoreTokenCache(_store, "github");
        var obtained = DateTimeOffset.UtcNow;

        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresIn = 3600,
            Scope = "mcp:tools repo:read",
            ObtainedAt = obtained,
        }, CancellationToken.None);

        var creds = await _store.GetAsync("github");
        creds!.OAuth.Should().NotBeNull();
        creds.OAuth!.AccessToken.Should().Be("access-1");
        creds.OAuth.RefreshToken.Should().Be("refresh-1");
        creds.OAuth.Scopes.Should().BeEquivalentTo(new[] { "mcp:tools", "repo:read" });
        creds.OAuth.ExpiresAtUtc.Should().NotBeNull();
        (creds.OAuth.ExpiresAtUtc!.Value - obtained).TotalSeconds.Should().BeApproximately(3600, 1);
    }

    [Fact]
    public async Task Round_trip_preserves_access_token()
    {
        var cache = new CredentialStoreTokenCache(_store, "github");
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "round-trip-access",
            RefreshToken = "round-trip-refresh",
            ExpiresIn = 1800,
            Scope = "mcp:tools",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("round-trip-access");
        loaded.RefreshToken.Should().Be("round-trip-refresh");
        loaded.Scope.Should().Be("mcp:tools");
    }

    [Fact]
    public async Task StoreTokensAsync_preserves_refresh_token_when_new_response_omits_it()
    {
        // OAuth refresh responses commonly omit the refresh_token. The cache must keep
        // the old one rather than wiping it.
        var cache = new CredentialStoreTokenCache(_store, "github");
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "first",
            RefreshToken = "long-lived-refresh",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Simulate a refresh: new access token, no refresh token in response.
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "second",
            RefreshToken = null,
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var creds = await _store.GetAsync("github");
        creds!.OAuth!.AccessToken.Should().Be("second");
        creds.OAuth.RefreshToken.Should().Be("long-lived-refresh");
    }

    [Fact]
    public async Task StoreTokensAsync_does_not_clobber_unrelated_credentials()
    {
        // A server might transition from bearer to oauth (or have both stored). The cache
        // shouldn't wipe non-oauth fields.
        await _store.SaveAsync("github", new ServerCredentials { BearerToken = "manual-bearer" });
        var cache = new CredentialStoreTokenCache(_store, "github");

        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "oauth-access",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var creds = await _store.GetAsync("github");
        creds!.BearerToken.Should().Be("manual-bearer");
        creds.OAuth!.AccessToken.Should().Be("oauth-access");
    }
}
