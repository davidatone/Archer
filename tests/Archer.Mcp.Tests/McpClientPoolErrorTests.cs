using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Client;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Mcp.Tests;

/// <summary>
/// Pool-level invariants that don't require a live MCP server. Connect-success behavior
/// is exercised by integration tests against a real container.
/// </summary>
public sealed class McpClientPoolErrorTests : IDisposable
{
    private readonly string _root;
    private readonly Registry.McpServerRegistry _registry;
    private readonly Credentials.EncryptedFileCredentialStore _creds;

    public McpClientPoolErrorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-pool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var mcpDir = Path.Combine(_root, "mcp");
        Directory.CreateDirectory(mcpDir);

        var dp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();

        _registry = Registry.McpServerRegistry.FromDirectories([mcpDir], mcpDir);
        _creds = new Credentials.EncryptedFileCredentialStore(
            Path.Combine(_root, "credentials.dat"), dp);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetAsync_throws_for_unknown_server()
    {
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        var act = async () => await pool.GetAsync("never-registered");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never-registered*");
    }

    [Fact]
    public async Task GetAsync_throws_for_disabled_server()
    {
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "off",
            Disabled = true,
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                Endpoint = new Uri("http://nowhere/mcp"),
            },
        });
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        var act = async () => await pool.GetAsync("off");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
    }

    [Fact]
    public async Task GetAsync_throws_for_stdio_with_no_command()
    {
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "no-command",
            Transport = new McpTransportConfig { Type = McpTransportType.Stdio },
        });
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        var act = async () => await pool.GetAsync("no-command");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transport.command*");
    }

    [Fact]
    public async Task GetAsync_throws_when_endpoint_is_missing_for_http_transport()
    {
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "no-endpoint",
            Transport = new McpTransportConfig { Type = McpTransportType.Sse },
        });
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        var act = async () => await pool.GetAsync("no-endpoint");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transport.endpoint*");
    }

    [Fact]
    public async Task First_caller_cancellation_does_not_poison_the_lazy_for_subsequent_callers()
    {
        // Regression: an earlier version of the pool baked the first caller's CancellationToken
        // into the Lazy<Task<McpClient>> factory. If that caller cancelled, every later caller
        // received the same TaskCanceledException forever, until someone explicitly evicted.
        await _registry.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "never-reachable",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.StreamableHttp,
                // Port 1 is never bound on a normal machine — the connect will hang or fail,
                // but importantly: connection failure is NOT cancellation.
                Endpoint = new Uri("http://127.0.0.1:1/mcp"),
            },
        });
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        // Caller A: pre-cancel so the throw is deterministic. (A timed cancellation race
        // against the OS's connect-refused signal varies wildly between machines: some return
        // ECONNREFUSED in microseconds and beat the timer.) An already-cancelled token still
        // exercises the same code path — the interesting question is whether B is poisoned.
        using var ctsA = new CancellationTokenSource();
        await ctsA.CancelAsync();
        var actA = async () => await pool.GetAsync("never-reachable", ctsA.Token);
        await actA.Should().ThrowAsync<OperationCanceledException>();

        // Caller B: arrives later with a longer timeout. The connect itself will eventually
        // fail (refused / timeout) — that's fine; we just need to verify B does NOT receive
        // an OperationCanceledException originating from A's cancellation.
        using var ctsB = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actB = async () => await pool.GetAsync("never-reachable", ctsB.Token);

        // Either a connection-related exception (preferred) or B's own timeout — neither is A's
        // OperationCanceledException. We assert it's NOT exactly A's cancellation token.
        try { await actB(); }
        catch (OperationCanceledException oce)
        {
            oce.CancellationToken.Should().NotBe(ctsA.Token,
                "B should not see A's cancellation token — that would mean the Lazy was poisoned");
        }
        catch
        {
            // Connect-related exception (HttpRequestException, etc.) is the happy path here:
            // the connect was attempted fresh on caller B's behalf rather than reusing A's
            // cancelled task.
        }
    }

    [Fact]
    public async Task EvictAsync_is_idempotent_for_unknown_server()
    {
        await using var pool = new McpClientPool(_registry, _creds, NullLoggerFactory.Instance);

        var act = async () => await pool.EvictAsync("never-cached");

        await act.Should().NotThrowAsync();
    }
}
