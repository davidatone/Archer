using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Mcp.Tests;

public sealed class OAuthOptionsFactoryTests : IDisposable
{
    private readonly string _root;
    private readonly Credentials.EncryptedFileCredentialStore _credentials;

    public OAuthOptionsFactoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-oauth-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var dp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        _credentials = new Credentials.EncryptedFileCredentialStore(
            Path.Combine(_root, "credentials.dat"), dp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Build_wires_token_cache_redirect_uri_and_scopes()
    {
        var server = ServerWith(authServerUri: null);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        options.TokenCache.Should().BeOfType<CredentialStoreTokenCache>();
        options.RedirectUri!.Scheme.Should().Be("http");
        options.RedirectUri.Host.Should().Be("localhost");
        options.RedirectUri.Port.Should().BeGreaterThan(0);
        options.RedirectUri.AbsolutePath.Should().Be("/callback/");
        options.Scopes.Should().BeEquivalentTo(new[] { "mcp:tools" });
    }

    [Fact]
    public void Build_enables_DCR_when_clientId_is_not_configured()
    {
        var server = ServerWith(authServerUri: null);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        options.ClientId.Should().BeNull();
        options.DynamicClientRegistration.Should().NotBeNull();
        options.DynamicClientRegistration!.ClientName.Should().Be("Archer");
    }

    [Fact]
    public void Build_skips_DCR_when_clientId_is_configured()
    {
        var server = ServerWith(authServerUri: null) with
        {
            Auth = new McpAuthConfig
            {
                Type = McpAuthType.OAuth,
                Scopes = ["mcp:tools"],
                ClientId = "preregistered-client",
            },
        };

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        options.ClientId.Should().Be("preregistered-client");
        options.DynamicClientRegistration.Should().BeNull();
    }

    [Fact]
    public void Build_does_not_set_AuthServerSelector_when_no_pinned_issuer()
    {
        var server = ServerWith(authServerUri: null);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        options.AuthServerSelector.Should().BeNull();
    }

    [Fact]
    public void AuthServerSelector_returns_the_pinned_server_when_present_in_PRM_list()
    {
        var pinned = new Uri("https://auth.example.com/realms/main");
        var server = ServerWith(authServerUri: pinned);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        var candidates = new[]
        {
            new Uri("https://other.example.com"),
            pinned,
            new Uri("https://third.example.com"),
        };
        var chosen = options.AuthServerSelector!.Invoke(candidates);

        chosen.Should().Be(pinned);
    }

    [Fact]
    public void AuthServerSelector_tolerates_trailing_slash_differences()
    {
        var configured = new Uri("https://auth.example.com/realms/main");
        var fromPRM = new Uri("https://auth.example.com/realms/main/");
        var server = ServerWith(authServerUri: configured);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);
        var chosen = options.AuthServerSelector!.Invoke([fromPRM]);

        chosen.Should().Be(fromPRM);
    }

    [Fact]
    public void AuthServerSelector_throws_when_pinned_issuer_is_not_in_PRM_list()
    {
        var pinned = new Uri("https://auth.example.com/realms/main");
        var server = ServerWith(authServerUri: pinned);

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        var act = () => options.AuthServerSelector!.Invoke([new Uri("https://rogue.example.com")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not in the protected resource metadata*");
    }

    [Fact]
    public void Build_uses_cached_client_id_when_present_and_skips_DCR()
    {
        var server = ServerWith(authServerUri: null);
        var cached = new ServerCredentials
        {
            OAuth = new OAuthTokens
            {
                AccessToken = "anything",
                ClientId = "dcr-registered-12345",
            },
        };

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: cached);

        options.ClientId.Should().Be("dcr-registered-12345");
        options.DynamicClientRegistration.Should().BeNull("DCR should not run when we have a cached client_id");
    }

    [Fact]
    public void Build_prefers_explicit_yaml_client_id_over_cached_one()
    {
        var server = ServerWith(authServerUri: null) with
        {
            Auth = new McpAuthConfig
            {
                Type = McpAuthType.OAuth,
                Scopes = ["mcp:tools"],
                ClientId = "yaml-client",
            },
        };
        var cached = new ServerCredentials
        {
            OAuth = new OAuthTokens { AccessToken = "x", ClientId = "stale-cached-client" },
        };

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: cached);

        options.ClientId.Should().Be("yaml-client");
    }

    [Fact]
    public void Build_uses_configured_redirect_port_when_nonzero()
    {
        var server = ServerWith(authServerUri: null) with
        {
            Auth = new McpAuthConfig
            {
                Type = McpAuthType.OAuth,
                Scopes = ["mcp:tools"],
                RedirectPort = 17181,
            },
        };

        var options = OAuthOptionsFactory.Build(server, _credentials, cachedCredentials: null);

        options.RedirectUri!.Port.Should().Be(17181);
    }

    private static McpServerConfig ServerWith(Uri? authServerUri) => new()
    {
        Name = "github",
        Transport = new McpTransportConfig
        {
            Type = McpTransportType.StreamableHttp,
            Endpoint = new Uri("https://api.example.com/mcp"),
        },
        Auth = new McpAuthConfig
        {
            Type = McpAuthType.OAuth,
            Scopes = ["mcp:tools"],
            AuthorizationServer = authServerUri,
        },
    };
}
