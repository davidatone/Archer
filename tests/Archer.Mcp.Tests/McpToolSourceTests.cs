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
using ModelContextProtocol.Client;

namespace Archer.Mcp.Tests;

/// <summary>
/// Validates the registry-change → enumerate → register pipeline in <see cref="McpToolSource"/>
/// without spawning a real MCP server. Live behavior is covered by
/// <see cref="LiveStdioMcpIntegrationTests"/>.
/// </summary>
public sealed class McpToolSourceTests : IDisposable
{
    private readonly string _root;
    private readonly Registry.McpServerRegistry _registry;
    private readonly Credentials.EncryptedFileCredentialStore _credentials;
    private readonly Archer.Tools.ToolRegistry _toolRegistry;

    public McpToolSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-toolsource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var dp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        var mcpDir = Path.Combine(_root, "mcp");
        Directory.CreateDirectory(mcpDir);
        _registry = Registry.McpServerRegistry.FromDirectories([mcpDir], mcpDir);
        _credentials = new Credentials.EncryptedFileCredentialStore(
            Path.Combine(_root, "credentials.dat"), dp);
        _toolRegistry = new Archer.Tools.ToolRegistry([], NullLogger<Archer.Tools.ToolRegistry>.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StartAsync_skips_server_that_needs_credentials_when_none_are_stored()
    {
        // api-key auth + no creds saved → SafeEnumerateAsync should short-circuit before
        // the pool is touched. Production behavior: no spurious connect-and-fail at startup.
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "needs-creds",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "cat",
            },
            Auth = new McpAuthConfig { Type = McpAuthType.ApiKey },
        });
        var pool = new RecordingPool();
        await using var source = new McpToolSource(_registry, _toolRegistry, pool, _credentials, NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        pool.GetAsyncCalls.Should().Be(0, "the source must not connect to a server that has no credentials");
        _toolRegistry.Definitions.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_skips_disabled_servers()
    {
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "off",
            Disabled = true,
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "cat",
            },
        });
        var pool = new RecordingPool();
        await using var source = new McpToolSource(_registry, _toolRegistry, pool, _credentials, NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        pool.GetAsyncCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_attempts_connect_for_servers_with_no_auth_required()
    {
        // auth: none + no creds needed → source should call pool.GetAsync. The fake pool
        // throws on connect; we verify the attempt happened and the failure was non-fatal.
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "open-server",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("http://localhost:1/mcp"),
            },
        });
        var pool = new RecordingPool { ThrowOnGet = new InvalidOperationException("simulated") };
        await using var source = new McpToolSource(_registry, _toolRegistry, pool, _credentials, NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        pool.GetAsyncCalls.Should().Be(1, "auth: none servers should be eagerly connected");
        _toolRegistry.Definitions.Should().BeEmpty("connect failed, so no tools should land");
    }

    [Fact]
    public async Task RefreshAsync_after_credentials_saved_evicts_and_reconnects()
    {
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "needs-creds",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("http://localhost:1/mcp"),
            },
            Auth = new McpAuthConfig { Type = McpAuthType.Bearer },
        });
        var pool = new RecordingPool { ThrowOnGet = new InvalidOperationException("simulated") };
        await using var source = new McpToolSource(_registry, _toolRegistry, pool, _credentials, NullLoggerFactory.Instance);

        // Start with no creds → skipped.
        await source.StartAsync(CancellationToken.None);
        pool.GetAsyncCalls.Should().Be(0);

        // Save creds and refresh — source should now attempt the connect.
        await _credentials.SaveAsync("needs-creds", new ServerCredentials { BearerToken = "test" });
        await source.RefreshAsync("needs-creds");

        pool.GetAsyncCalls.Should().BeGreaterThan(0,
            "Refresh after credentials are saved must trigger a connect attempt");
    }

    [Fact]
    public async Task Removed_event_for_unknown_server_is_a_noop()
    {
        var pool = new RecordingPool();
        await using var source = new McpToolSource(_registry, _toolRegistry, pool, _credentials, NullLoggerFactory.Instance);
        await source.StartAsync(CancellationToken.None);

        // Refresh a server that was never registered — should not throw.
        var act = async () => await source.RefreshAsync("never-existed");

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Records pool calls so tests can assert on attempt counts. Throws on GetAsync (we
    /// can't synthesize a real <see cref="McpClient"/> without a transport, and the source's
    /// failure path is exactly what we want to verify in unit tests).
    /// </summary>
    private sealed class RecordingPool : IMcpClientPool
    {
        public int GetAsyncCalls;
        public int EvictAsyncCalls;
        public Exception? ThrowOnGet { get; set; }

        public ValueTask<McpClient> GetAsync(string serverName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref GetAsyncCalls);
            throw ThrowOnGet ?? new InvalidOperationException("not configured");
        }

        public ValueTask EvictAsync(string serverName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref EvictAsyncCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
