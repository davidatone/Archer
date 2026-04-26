using Archer.Domain.Mcp;
using Archer.Mcp.Client;
using FluentAssertions;

namespace Archer.Mcp.Tests;

public class EnvResolverTests
{
    [Fact]
    public void Resolves_env_placeholder_from_process_environment()
    {
        Environment.SetEnvironmentVariable("ARCHER_TEST_RESOLVER", "from-env");
        try
        {
            var warnings = new List<string>();
            EnvResolver.Resolve("hello ${env:ARCHER_TEST_RESOLVER}", null, warnings)
                .Should().Be("hello from-env");
            warnings.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARCHER_TEST_RESOLVER", null);
        }
    }

    [Fact]
    public void Resolves_cred_apiKey_fields_from_credentials()
    {
        var creds = new ServerCredentials
        {
            ApiKey = new ApiKeyPair { Key = "k-123", Token = "t-456" },
        };

        var warnings = new List<string>();
        EnvResolver.Resolve("${cred:apiKey.key}", creds, warnings).Should().Be("k-123");
        EnvResolver.Resolve("${cred:apiKey.token}", creds, warnings).Should().Be("t-456");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_cred_bearer_and_oauth_access_tokens()
    {
        var creds = new ServerCredentials
        {
            BearerToken = "bearer-xyz",
            OAuth = new OAuthTokens { AccessToken = "oauth-abc" },
        };

        var warnings = new List<string>();
        EnvResolver.Resolve("${cred:bearerToken}", creds, warnings).Should().Be("bearer-xyz");
        EnvResolver.Resolve("${cred:oauth.accessToken}", creds, warnings).Should().Be("oauth-abc");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Replaces_unknown_placeholder_with_empty_and_collects_warning()
    {
        var warnings = new List<string>();

        EnvResolver.Resolve("[${cred:apiKey.key}]", null, warnings).Should().Be("[]");

        warnings.Should().ContainSingle().Which.Should().Contain("apiKey.key");
    }

    [Fact]
    public void Multiple_placeholders_are_each_resolved_independently()
    {
        Environment.SetEnvironmentVariable("ARCHER_RESOLVE_ONE", "one");
        Environment.SetEnvironmentVariable("ARCHER_RESOLVE_TWO", "two");
        try
        {
            var warnings = new List<string>();
            EnvResolver.Resolve("${env:ARCHER_RESOLVE_ONE}-${env:ARCHER_RESOLVE_TWO}", null, warnings)
                .Should().Be("one-two");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARCHER_RESOLVE_ONE", null);
            Environment.SetEnvironmentVariable("ARCHER_RESOLVE_TWO", null);
        }
    }

    [Fact]
    public void ResolveAll_walks_a_dictionary_of_values()
    {
        var creds = new ServerCredentials
        {
            ApiKey = new ApiKeyPair { Key = "k", Token = "t" },
        };
        var source = new Dictionary<string, string>
        {
            ["TRELLO_API_KEY"] = "${cred:apiKey.key}",
            ["TRELLO_TOKEN"] = "${cred:apiKey.token}",
            ["LITERAL"] = "no-placeholders-here",
        };

        var warnings = new List<string>();
        var resolved = EnvResolver.ResolveAll(source, creds, warnings);

        resolved["TRELLO_API_KEY"].Should().Be("k");
        resolved["TRELLO_TOKEN"].Should().Be("t");
        resolved["LITERAL"].Should().Be("no-placeholders-here");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Cred_placeholder_with_no_credentials_collects_warning()
    {
        var warnings = new List<string>();

        EnvResolver.Resolve("${cred:apiKey.key}", credentials: null, warnings).Should().Be(string.Empty);

        warnings.Should().ContainSingle().Which.Should().Contain("no credentials available");
    }

    [Fact]
    public void Strings_without_placeholders_pass_through_unchanged()
    {
        var warnings = new List<string>();
        EnvResolver.Resolve("plain-text-value", null, warnings).Should().Be("plain-text-value");
        warnings.Should().BeEmpty();
    }
}
