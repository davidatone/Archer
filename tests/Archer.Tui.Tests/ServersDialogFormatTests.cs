using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Tui.Ui;
using FluentAssertions;

namespace Archer.Tui.Tests;

public class ServersDialogFormatTests
{
    [Theory]
    [InlineData(McpAuthType.None, false, "-")]
    [InlineData(McpAuthType.None, true, "-")]
    [InlineData(McpAuthType.Bearer, true, "*")]
    [InlineData(McpAuthType.Bearer, false, "?")]
    [InlineData(McpAuthType.OAuth, true, "*")]
    [InlineData(McpAuthType.OAuth, false, "?")]
    [InlineData(McpAuthType.ApiKey, true, "*")]
    [InlineData(McpAuthType.ApiKey, false, "?")]
    public void FormatCredsMarker_uses_dash_for_none_and_star_or_question(
        McpAuthType type, bool hasCreds, string expected)
    {
        ServersDialog.FormatCredsMarker(type, hasCreds).Should().Be(expected);
    }

    [Fact]
    public void FormatEndpoint_renders_http_url()
    {
        var cfg = new McpServerConfig
        {
            Name = "atlassian",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("https://example.test/mcp"),
            },
        };
        ServersDialog.FormatEndpoint(cfg).Should().Be("https://example.test/mcp");
    }

    [Fact]
    public void FormatEndpoint_renders_stdio_command_with_args()
    {
        var cfg = new McpServerConfig
        {
            Name = "trello",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "node",
                Args = ["server.js", "--port=3000"],
            },
        };
        ServersDialog.FormatEndpoint(cfg)
            .Should().Be("node server.js --port=3000");
    }

    [Fact]
    public void FormatEndpoint_returns_none_when_no_endpoint_or_command()
    {
        var cfg = new McpServerConfig
        {
            Name = "x",
            Transport = new McpTransportConfig { Type = McpTransportType.Stdio },
        };
        ServersDialog.FormatEndpoint(cfg).Should().Be("(none)");
    }

    [Fact]
    public void FormatEndpoint_truncates_long_endpoints_with_ellipsis()
    {
        var cfg = new McpServerConfig
        {
            Name = "x",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("https://very-long-host.example.com/" + new string('a', 60)),
            },
        };
        var s = ServersDialog.FormatEndpoint(cfg);
        s.Length.Should().Be(38);
        s.Should().EndWith("...");
    }

    [Theory]
    [InlineData(McpAuthType.Bearer, "abc", "", true)]
    [InlineData(McpAuthType.Bearer, "  ", "", false)]
    [InlineData(McpAuthType.Bearer, "abc", "ignored", true)]
    [InlineData(McpAuthType.ApiKey, "key", "token", true)]
    [InlineData(McpAuthType.ApiKey, "key", "", false)]
    [InlineData(McpAuthType.ApiKey, "", "token", false)]
    [InlineData(McpAuthType.ApiKey, " ", " ", false)]
    public void TryBuildCredentialsFromStrings_validates_inputs_per_auth_type(
        McpAuthType type, string p, string s, bool expected)
    {
        var ok = ServersDialog.TryBuildCredentialsFromStrings(type, p, s, out _);
        ok.Should().Be(expected);
    }

    [Fact]
    public void TryBuildCredentialsFromStrings_bearer_populates_token()
    {
        ServersDialog.TryBuildCredentialsFromStrings(McpAuthType.Bearer, "  abc  ", "", out var creds)
            .Should().BeTrue();
        creds.BearerToken.Should().Be("abc");
        creds.ApiKey.Should().BeNull();
    }

    [Fact]
    public void TryBuildCredentialsFromStrings_apikey_populates_pair()
    {
        ServersDialog.TryBuildCredentialsFromStrings(McpAuthType.ApiKey, "k", "t", out var creds)
            .Should().BeTrue();
        creds.ApiKey.Should().NotBeNull();
        creds.ApiKey!.Key.Should().Be("k");
        creds.ApiKey.Token.Should().Be("t");
        creds.BearerToken.Should().BeNull();
    }

    [Fact]
    public void FormatConnectionState_returns_em_dash_when_status_unknown()
    {
        ServersDialog.FormatConnectionState(null).Should().Be("—");
    }

    [Theory]
    [InlineData(McpConnectionState.NotAttempted, "pending")]
    [InlineData(McpConnectionState.Connecting, "connecting…")]
    [InlineData(McpConnectionState.Failed, "failed")]
    [InlineData(McpConnectionState.NeedsCredentials, "no-creds")]
    [InlineData(McpConnectionState.Disabled, "disabled")]
    public void FormatConnectionState_uses_short_label_per_state(McpConnectionState state, string expected)
    {
        var status = new McpServerStatus
        {
            ServerName = "x", State = state, ToolCount = 0, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        ServersDialog.FormatConnectionState(status).Should().Be(expected);
    }

    [Fact]
    public void FormatConnectionState_includes_tool_count_when_connected()
    {
        var status = new McpServerStatus
        {
            ServerName = "x", State = McpConnectionState.Connected, ToolCount = 17,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        ServersDialog.FormatConnectionState(status).Should().Be("ok (17)");
    }

    [Fact]
    public void FormatRow_aligns_columns()
    {
        var cfg = new McpServerConfig
        {
            Name = "memory",
            Transport = new McpTransportConfig { Type = McpTransportType.Stdio, Command = "npx" },
            Auth = new McpAuthConfig { Type = McpAuthType.None },
        };
        var row = ServersDialog.FormatRow(cfg, hasCreds: false);
        row.Should().StartWith(" memory");
        // Auth marker for None is "-" — must not appear blank.
        row.Should().Contain(" - ");
    }
}
