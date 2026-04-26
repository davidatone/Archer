using Archer.Domain.Mcp;
using Archer.Mcp.Yaml;
using FluentAssertions;

namespace Archer.Mcp.Tests;

public class YamlMcpConfigLoaderTests
{
    

    [Fact]
    public void Load_minimal_config_with_no_auth_block()
    {
        const string yaml = """
            name: simplemem
            transport:
              type: streamable-http
              endpoint: http://simplemem:8000/mcp
            """;

        var config = YamlMcpConfigLoader.Load(new StringReader(yaml));

        config.Name.Should().Be("simplemem");
        config.Transport.Type.Should().Be(McpTransportType.StreamableHttp);
        config.Transport.Endpoint!.AbsoluteUri.Should().Be("http://simplemem:8000/mcp");
        config.Auth.Type.Should().Be(McpAuthType.None);
        config.Disabled.Should().BeFalse();
    }

    [Fact]
    public void Load_full_config_with_api_key_auth_and_tool_overrides()
    {
        const string yaml = """
            name: trello
            description: Trello workspace.
            disabled: false
            transport:
              type: sse
              endpoint: http://trello-mcp:8000/sse
            auth:
              type: api-key
            timeouts:
              connectSeconds: 10
              callSeconds: 30
            retry:
              maxAttempts: 5
              initialDelayMs: 250
              backoff: constant
            tools:
              - name: add_card_to_list
                descriptionOverride: "Add a card to a list."
              - name: list_boards
            """;

        var config = YamlMcpConfigLoader.Load(new StringReader(yaml));

        config.Transport.Type.Should().Be(McpTransportType.Sse);
        config.Auth.Type.Should().Be(McpAuthType.ApiKey);
        config.Timeouts.ConnectSeconds.Should().Be(10);
        config.Retry.MaxAttempts.Should().Be(5);
        config.Retry.Backoff.Should().Be(McpRetryBackoff.Constant);
        config.ToolOverrides.Should().HaveCount(2);
        config.ToolOverrides[0].DescriptionOverride.Should().Be("Add a card to a list.");
        config.ToolOverrides[1].DescriptionOverride.Should().BeNull();
    }

    [Fact]
    public void Load_oauth_config_with_scopes_and_pre_registered_client()
    {
        const string yaml = """
            name: github
            transport:
              type: streamable-http
              endpoint: https://api.githubcopilot.com/mcp/
            auth:
              type: oauth
              scopes: [mcp:tools, repo:read]
              authorizationServer: https://github.com/login/oauth
              clientId: archer-cli
              redirectPort: 17181
            """;

        var config = YamlMcpConfigLoader.Load(new StringReader(yaml));

        config.Auth.Type.Should().Be(McpAuthType.OAuth);
        config.Auth.Scopes.Should().BeEquivalentTo(new[] { "mcp:tools", "repo:read" });
        config.Auth.AuthorizationServer!.Host.Should().Be("github.com");
        config.Auth.ClientId.Should().Be("archer-cli");
        config.Auth.RedirectPort.Should().Be(17181);
    }

    [Fact]
    public void Load_stdio_transport_with_command_and_env()
    {
        const string yaml = """
            name: trello-stdio
            transport:
              type: stdio
              command: bunx
              args: ["-y", "@delorenj/mcp-server-trello"]
              env:
                TRELLO_API_KEY: from-aspire
            auth:
              type: none
            """;

        var config = YamlMcpConfigLoader.Load(new StringReader(yaml));

        config.Transport.Type.Should().Be(McpTransportType.Stdio);
        config.Transport.Command.Should().Be("bunx");
        config.Transport.Args.Should().BeEquivalentTo(new[] { "-y", "@delorenj/mcp-server-trello" });
        config.Transport.Env["TRELLO_API_KEY"].Should().Be("from-aspire");
    }

    [Fact]
    public void Reject_literal_secret_in_auth_token()
    {
        const string yaml = """
            name: bad
            transport: { type: streamable-http, endpoint: http://x/mcp }
            auth:
              type: bearer
              token: literal-secret-do-not-do-this
            """;

        var act = () => YamlMcpConfigLoader.Load(new StringReader(yaml));

        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*literal value found in 'token'*credential store*");
    }

    [Fact]
    public void Allow_env_var_reference_in_auth_token()
    {
        const string yaml = """
            name: ok
            transport: { type: streamable-http, endpoint: http://x/mcp }
            auth:
              type: bearer
              token: ${env:MY_TOKEN}
            """;

        var config = YamlMcpConfigLoader.Load(new StringReader(yaml));

        config.Auth.Type.Should().Be(McpAuthType.Bearer);
    }

    [Fact]
    public void Reject_unknown_transport_type()
    {
        const string yaml = """
            name: x
            transport: { type: telepathy, endpoint: http://x }
            """;

        var act = () => YamlMcpConfigLoader.Load(new StringReader(yaml));

        act.Should().Throw<InvalidDataException>().WithMessage("*unknown transport type 'telepathy'*");
    }

    [Fact]
    public void Reject_invalid_name_with_dot_or_uppercase()
    {
        var actDot = () => YamlMcpConfigLoader.Load(new StringReader("""
            name: my.server
            transport: { type: stdio, command: x }
            """));
        var actUpper = () => YamlMcpConfigLoader.Load(new StringReader("""
            name: MyServer
            transport: { type: stdio, command: x }
            """));

        actDot.Should().Throw<InvalidDataException>().WithMessage("*[a-z0-9_-]+*");
        actUpper.Should().Throw<InvalidDataException>().WithMessage("*[a-z0-9_-]+*");
    }

    [Fact]
    public void Round_trip_serialize_then_load_preserves_fields()
    {
        var original = new McpServerConfig
        {
            Name = "trello",
            Description = "Trello workspace.",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("http://trello-mcp:8000/sse"),
                Headers = new Dictionary<string, string> { ["X-Custom"] = "value" },
            },
            Auth = new McpAuthConfig { Type = McpAuthType.ApiKey },
            Timeouts = new McpTimeouts { ConnectSeconds = 5, CallSeconds = 15 },
            Retry = new McpRetryPolicy { MaxAttempts = 7, InitialDelayMs = 100, Backoff = McpRetryBackoff.Constant },
            ToolOverrides =
            [
                new McpToolOverride { Name = "list_boards", DescriptionOverride = "List Trello boards." },
            ],
        };

        var yaml = YamlMcpConfigLoader.Serialize(original);
        var roundTripped = YamlMcpConfigLoader.Load(new StringReader(yaml));

        roundTripped.Should().BeEquivalentTo(original, opts => opts
            .Excluding(c => c.SourcePath));
    }
}
