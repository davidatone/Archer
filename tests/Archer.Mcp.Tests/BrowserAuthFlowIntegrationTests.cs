using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Archer.Mcp.Auth;
using FluentAssertions;

namespace Archer.Mcp.Tests;

/// <summary>
/// Real integration test: spins up <see cref="BrowserAuthFlow"/> against a live
/// <see cref="HttpListener"/>, hits the redirect URI from a separate task, and verifies
/// the captured authorization code round-trips. Doesn't open a real browser
/// (auto-open is best-effort and we just confirm it doesn't throw).
/// </summary>
public class BrowserAuthFlowIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HandleAsync_returns_code_when_redirect_arrives_with_code_param()
    {
        var port = FindFreePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        var authUri = new Uri($"http://localhost:{port}/skip-the-real-browser?state=xyz");
        var flow = new BrowserAuthFlow();

        using var cts = new CancellationTokenSource(TestTimeout);
        var flowTask = flow.HandleAsync(authUri, redirectUri, cts.Token);

        // Hit the local listener as if the browser had completed the dance.
        await SimulateBrowserCallbackAsync(port, "code=captured-12345&state=xyz", cts.Token);

        var captured = await flowTask;
        captured.Should().Be("captured-12345");
    }

    [Fact]
    public async Task HandleAsync_throws_when_callback_carries_error()
    {
        var port = FindFreePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        var authUri = new Uri($"http://localhost:{port}/skip");
        var flow = new BrowserAuthFlow();

        using var cts = new CancellationTokenSource(TestTimeout);
        var flowTask = flow.HandleAsync(authUri, redirectUri, cts.Token);

        await SimulateBrowserCallbackAsync(port, "error=access_denied&error_description=user+declined", cts.Token);

        var act = async () => await flowTask;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*access_denied*user declined*");
    }

    [Fact]
    public async Task HandleAsync_throws_when_callback_arrives_without_code()
    {
        var port = FindFreePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        var authUri = new Uri($"http://localhost:{port}/skip");
        var flow = new BrowserAuthFlow();

        using var cts = new CancellationTokenSource(TestTimeout);
        var flowTask = flow.HandleAsync(authUri, redirectUri, cts.Token);

        await SimulateBrowserCallbackAsync(port, "state=only", cts.Token);

        var act = async () => await flowTask;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'code' parameter*");
    }

    [Fact]
    public async Task HandleAsync_serves_a_success_page_in_the_browser()
    {
        var port = FindFreePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        var authUri = new Uri($"http://localhost:{port}/skip");
        var flow = new BrowserAuthFlow();

        using var cts = new CancellationTokenSource(TestTimeout);
        var flowTask = flow.HandleAsync(authUri, redirectUri, cts.Token);

        using var http = new HttpClient { Timeout = TestTimeout };
        var response = await http.GetAsync($"http://localhost:{port}/callback/?code=ok&state=s", cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        await flowTask;  // ensure the listener has shut down cleanly

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Authorization complete");
        body.Should().Contain("close this tab");
    }

    [Fact]
    public async Task HandleAsync_propagates_cancellation_when_caller_cancels()
    {
        var port = FindFreePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        var authUri = new Uri($"http://localhost:{port}/skip");
        var flow = new BrowserAuthFlow();

        using var cts = new CancellationTokenSource();
        var flowTask = flow.HandleAsync(authUri, redirectUri, cts.Token);

        cts.Cancel();

        var act = async () => await flowTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- helpers --------------------------------------------------------------------

    private static async Task SimulateBrowserCallbackAsync(int port, string queryString, CancellationToken ct)
    {
        // Race the listener's bind window — retry briefly so the test isn't flaky.
        using var http = new HttpClient { Timeout = TestTimeout };
        Exception? lastErr = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await http.GetAsync($"http://localhost:{port}/callback/?{queryString}", ct);
                return;
            }
            catch (HttpRequestException ex)
            {
                lastErr = ex;
                await Task.Delay(20, ct);
            }
        }
        throw new InvalidOperationException("Listener never became reachable.", lastErr);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
