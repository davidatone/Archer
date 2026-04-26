using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archer.Mcp.Auth;

/// <summary>
/// Implements the MCP SDK's <c>AuthorizationRedirectDelegate</c> by spinning up a local
/// <see cref="HttpListener"/> on the supplied redirect URI, opening the user's browser
/// to the authorization URI, and waiting for the redirect callback. Returns the
/// captured <c>code</c> query parameter.
/// </summary>
/// <remarks>
/// We always print the authorization URL alongside auto-opening the browser — this keeps
/// the flow usable when running headless / over SSH / in a container where
/// <c>open</c>/<c>xdg-open</c>/<c>start</c> can't reach a real browser.
/// </remarks>
public sealed class BrowserAuthFlow
{
    private readonly ILogger _logger;

    public BrowserAuthFlow(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<string?> HandleAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(redirectUri);

        // HttpListener wants the prefix to end with a trailing slash and use a wildcard host
        // when binding to localhost — we keep the host but normalize the path.
        var prefix = NormalizePrefix(redirectUri);
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Could not bind a local HTTP listener at {prefix}. " +
                "Pick a free port via auth.redirectPort in the server YAML, or close whatever " +
                "is using this port.", ex);
        }

        WriteUserPrompt(authorizationUri);
        TryOpenBrowser(authorizationUri);

        using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* best effort */ }
        });

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? string.Empty);
        var code = query["code"];
        var error = query["error"];

        await WriteCallbackResponseAsync(ctx.Response, error, code).ConfigureAwait(false);
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
        {
            var description = query["error_description"] ?? string.Empty;
            throw new InvalidOperationException(
                $"Authorization server returned error '{error}'" +
                (string.IsNullOrEmpty(description) ? "." : $": {description}"));
        }
        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException(
                "Authorization callback did not include a 'code' parameter.");
        }

        return code;
    }

    private static string NormalizePrefix(Uri redirectUri)
    {
        var builder = new UriBuilder(redirectUri);
        if (string.IsNullOrEmpty(builder.Path) || builder.Path == "/")
        {
            builder.Path = "/";
        }
        else if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }
        return builder.Uri.ToString();
    }

    private static void WriteUserPrompt(Uri authorizationUri)
    {
        // Print to stderr so it's visible even when stdout is being captured/piped.
        Console.Error.WriteLine();
        Console.Error.WriteLine("Open this URL in your browser to authorize:");
        Console.Error.WriteLine($"  {authorizationUri}");
        Console.Error.WriteLine("Waiting for redirect…");
        Console.Error.WriteLine();
    }

    private void TryOpenBrowser(Uri authorizationUri)
    {
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("open", authorizationUri.ToString());
            }
            else if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo("cmd", $"/c start \"\" \"{authorizationUri}\"")
                {
                    CreateNoWindow = true,
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                info = new ProcessStartInfo("xdg-open", authorizationUri.ToString());
            }
            else
            {
                _logger.LogInformation(
                    "Cannot auto-open browser on this platform. Use the URL printed above.");
                return;
            }
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using var proc = Process.Start(info);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Could not auto-open the browser. Use the URL printed above.");
        }
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, string? error, string? code)
    {
        var success = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code);
        var html = success
            ? "<!doctype html><meta charset=utf-8><title>Authorization complete</title>" +
              "<style>body{font-family:system-ui;text-align:center;margin-top:6em;color:#1d3557}</style>" +
              "<h1>Authorization complete</h1><p>You can close this tab and return to your terminal.</p>"
            : $"<!doctype html><meta charset=utf-8><title>Authorization failed</title>" +
              $"<style>body{{font-family:system-ui;text-align:center;margin-top:6em;color:#9d0208}}</style>" +
              $"<h1>Authorization failed</h1><p>{System.Net.WebUtility.HtmlEncode(error ?? "missing 'code' parameter")}</p>";

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = success ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }
}
