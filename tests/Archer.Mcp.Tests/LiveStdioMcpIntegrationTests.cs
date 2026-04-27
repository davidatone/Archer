using System.Text.Json.Nodes;
using Archer.Application.Mcp;
using Archer.Application.Tools;
using Archer.Domain.Mcp;
using Archer.Domain.Tools;
using Archer.Mcp.Client;
using Archer.Mcp.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Mcp.Tests;

/// <summary>
/// Live, end-to-end exercise of the MCP client stack against
/// <c>@modelcontextprotocol/server-everything</c> — the canonical reference MCP server,
/// spawned over stdio via <c>npx</c>. Tagged <c>LiveServer</c> so it can be opted out of
/// on machines without npx (filter: <c>--filter Category!=LiveServer</c>).
/// </summary>
[Trait("Category", "LiveServer")]
public sealed class LiveStdioMcpIntegrationTests : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    private readonly string _root;
    private readonly Registry.McpServerRegistry _registry;
    private readonly Credentials.EncryptedFileCredentialStore _credentials;

    public LiveStdioMcpIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-live-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var dp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-live-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        var mcpDir = Path.Combine(_root, "mcp");
        Directory.CreateDirectory(mcpDir);
        _registry = Registry.McpServerRegistry.FromDirectories([mcpDir], mcpDir);
        _credentials = new Credentials.EncryptedFileCredentialStore(
            Path.Combine(_root, "credentials.dat"), dp);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public async Task Connect_and_list_tools_against_server_everything()
    {
        Skip.IfNot(NpxAvailable(), "npx is not on PATH; skipping live MCP integration test.");

        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "everything",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-everything"],
            },
        });

        await using var pool = new McpClientPool(_registry, _credentials, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(ConnectTimeout);

        var client = await pool.GetAsync("everything", cts.Token);
        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

        tools.Should().NotBeEmpty("the reference test server advertises a fixed set of tools");
        // 'echo' has been a stable fixture of server-everything for years.
        tools.Select(t => t.Name).Should().Contain(name =>
            name.Equals("echo", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task End_to_end_through_the_tool_source_and_registry()
    {
        Skip.IfNot(NpxAvailable(), "npx is not on PATH; skipping live MCP integration test.");

        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "everything",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-everything"],
            },
        });

        var toolRegistry = new Archer.Tools.ToolRegistry([], NullLogger<Archer.Tools.ToolRegistry>.Instance);
        await using var pool = new McpClientPool(_registry, _credentials, NullLoggerFactory.Instance);
        await using var source = new McpToolSource(_registry, toolRegistry, pool, _credentials, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(ConnectTimeout);
        await source.StartAsync(cts.Token);
        await source.StartupCompletion;

        // server-everything's tools should now appear in the IToolRegistry under wire names.
        toolRegistry.Definitions.Should().NotBeEmpty();
        toolRegistry.Definitions.Select(d => d.Name).Should()
            .Contain(name => name.StartsWith("everything__", StringComparison.Ordinal));

        // Logical-form lookup resolves through the registry to the wire name.
        toolRegistry.TryGet("everything.echo", out var echoTool).Should().BeTrue();
        echoTool.Name.Should().Be("everything__echo");

        // Invoke 'echo' through the adapter (real bytes on the wire to the npx process).
        var args = new JsonObject { ["message"] = "hello-archer-integration-test" };
        var result = await toolRegistry.ExecuteAsync(new ToolRequest(
            ToolCallId: "call-1",
            ToolName: echoTool.Name,
            Arguments: args,
            RepoRoot: _root,
            AgentId: "test-agent"), cts.Token);

        result.Success.Should().BeTrue($"server-everything's echo() should succeed; got error: {result.Error}");
        result.Summary.Should().Contain("hello-archer-integration-test");

        await source.StopAsync(cts.Token);
    }

    private static bool NpxAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("npx", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            return proc.WaitForExit(5000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
