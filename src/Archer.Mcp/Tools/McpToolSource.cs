using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archer.Application.Mcp;
using Archer.Application.Tools;
using Archer.Domain.Mcp;
using Archer.Mcp.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Archer.Mcp.Tools;

/// <summary>
/// Hosts the bridge between the MCP server registry and the live <see cref="IToolRegistry"/>:
/// at startup connects each enabled server, enumerates its tools, registers a
/// <see cref="McpToolAdapter"/> for each. Subscribes to registry changes so runtime-added
/// servers' tools become visible without restart, and removed servers' tools disappear.
/// Connection failures are logged and non-fatal — the rest of Archer keeps running.
/// </summary>
public sealed class McpToolSource : IHostedService, IAsyncDisposable
{
    private readonly IMcpServerRegistry _serverRegistry;
    private readonly IToolRegistry _toolRegistry;
    private readonly IMcpClientPool _pool;
    private readonly ICredentialStore _credentials;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolSource> _logger;

    private readonly ConcurrentDictionary<string, HashSet<string>> _toolsByServer =
        new(StringComparer.Ordinal);

    /// <summary>Per-server connection state. Updated atomically as a connect attempt
    /// progresses NotAttempted → Connecting → (Connected | Failed | NeedsCredentials | Disabled).</summary>
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _backgroundCts = new();
    private Task _backgroundEnumeration = Task.CompletedTask;

    private Action<McpRegistryChange>? _registryHandler;

    /// <summary>Snapshot of every known server's connection state.</summary>
    public IReadOnlyDictionary<string, McpServerStatus> Statuses => _statuses;

    /// <summary>Fires whenever a server's connection state transitions. Invoked on the
    /// thread that performed the transition; subscribers must be thread-safe.</summary>
    public event Action<McpServerStatus>? StatusChanged;

    /// <summary>
    /// Completes when the initial post-<see cref="StartAsync"/> connect sweep has finished
    /// (every server settled to one of Connected / Failed / NeedsCredentials / Disabled).
    /// Used by integration tests that need to assert on the post-startup state.
    /// </summary>
    public Task StartupCompletion => _backgroundEnumeration;

    public McpToolSource(
        IMcpServerRegistry serverRegistry,
        IToolRegistry toolRegistry,
        IMcpClientPool pool,
        ICredentialStore credentials,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(serverRegistry);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(credentials);
        _serverRegistry = serverRegistry;
        _toolRegistry = toolRegistry;
        _pool = pool;
        _credentials = credentials;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<McpToolSource>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registryHandler = HandleServerChange;
        _serverRegistry.Changed += _registryHandler;

        // Seed every known server's status so the UI has something to render even before the
        // first connect attempt completes. Disabled servers go straight to Disabled; auth-required
        // servers without saved creds go to NeedsCredentials; the rest start at NotAttempted.
        foreach (var s in _serverRegistry.All)
        {
            UpdateStatus(s.Name, s.Disabled ? McpConnectionState.Disabled : McpConnectionState.NotAttempted, 0, null);
        }

        // Kick off the connect/enumeration sweep in the background. We deliberately do *not*
        // await — host startup must not block on a slow OAuth dance or an unreachable HTTP server.
        // Each server's status transitions through the connecting state and settles to one of the
        // terminal states defined in McpConnectionState; consumers subscribe via StatusChanged.
        var servers = _serverRegistry.All
            .Where(s => !s.Disabled)
            .ToList();

        _backgroundEnumeration = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(servers.Select(s => SafeEnumerateAsync(s, _backgroundCts.Token)))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Last-resort guard — individual SafeEnumerateAsync calls already trap their own
                // exceptions, so reaching here means a bug in the orchestration itself.
                _logger.LogError(ex, "Unexpected failure during initial MCP server enumeration sweep.");
            }
        }, _backgroundCts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registryHandler is not null)
        {
            _serverRegistry.Changed -= _registryHandler;
            _registryHandler = null;
        }

        // Cancel any still-running enumerations and give them a brief window to unwind.
        await _backgroundCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _backgroundEnumeration.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Best effort — we're shutting down anyway.
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    private void UpdateStatus(string serverName, McpConnectionState state, int toolCount, string? error)
    {
        var status = new McpServerStatus
        {
            ServerName = serverName,
            State = state,
            ToolCount = toolCount,
            Error = error,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _statuses[serverName] = status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex)
        {
            // Subscriber threw — don't let a bad subscriber break the host.
            _logger.LogWarning(ex, "McpToolSource.StatusChanged subscriber threw for '{Server}'.", serverName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
        _backgroundCts.Dispose();
    }

    /// <summary>
    /// Drop any cached client + registered tools for a server, then re-enumerate from scratch.
    /// Call after credentials change so newly-supplied auth takes effect without a process restart.
    /// </summary>
    public async Task RefreshAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        var config = _serverRegistry.Get(serverName);
        if (config is null)
        {
            return;
        }
        await UnregisterServerToolsAsync(serverName).ConfigureAwait(false);
        if (!config.Disabled)
        {
            await SafeEnumerateAsync(config, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- registry change handling -----------------------------------------------------

    private void HandleServerChange(McpRegistryChange change)
    {
        // Fire-and-forget — registry events are sync; we don't want to block the registry
        // mutation thread on network calls. Log any unobserved exception.
        _ = Task.Run(async () =>
        {
            try
            {
                switch (change.Kind)
                {
                    case McpRegistryChangeKind.Added:
                    case McpRegistryChangeKind.Updated:
                        await UnregisterServerToolsAsync(change.Server.Name).ConfigureAwait(false);
                        if (change.Server.Disabled)
                        {
                            UpdateStatus(change.Server.Name, McpConnectionState.Disabled, 0, null);
                        }
                        else
                        {
                            await SafeEnumerateAsync(change.Server, CancellationToken.None).ConfigureAwait(false);
                        }
                        break;

                    case McpRegistryChangeKind.Removed:
                        await UnregisterServerToolsAsync(change.Server.Name).ConfigureAwait(false);
                        _statuses.TryRemove(change.Server.Name, out _);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling registry change for {Server}", change.Server.Name);
            }
        });
    }

    private async Task SafeEnumerateAsync(McpServerConfig server, CancellationToken ct)
    {
        // Skip eager-connect for servers that need credentials we don't have yet. Avoids
        // spurious "auth required" failures on every CLI invocation when the user hasn't
        // run `archer mcp credentials set <server>` yet. The server will pick up tools
        // automatically on the next registry change once creds are saved (or on next start).
        if (server.Auth.Type is McpAuthType.Bearer or McpAuthType.ApiKey or McpAuthType.OAuth)
        {
            var creds = await _credentials.GetAsync(server.Name, ct).ConfigureAwait(false);
            if (!HasCredentialsFor(server.Auth.Type, creds))
            {
                _logger.LogInformation(
                    "MCP server '{Server}' requires {Auth} credentials — skipping eager connect. " +
                    "Run `archer mcp credentials set <server>` to configure.",
                    server.Name, server.Auth.Type.ToString().ToLowerInvariant());
                UpdateStatus(server.Name, McpConnectionState.NeedsCredentials, 0, null);
                return;
            }
        }

        UpdateStatus(server.Name, McpConnectionState.Connecting, 0, null);
        try
        {
            await EnumerateAndRegisterAsync(server, ct).ConfigureAwait(false);
            var toolCount = _toolsByServer.TryGetValue(server.Name, out var set) ? set.Count : 0;
            UpdateStatus(server.Name, McpConnectionState.Connected, toolCount, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — leave status alone; we're going away.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enumerate tools for MCP server '{Server}'. Tools from this server are " +
                "unavailable until the next reload.", server.Name);
            UpdateStatus(server.Name, McpConnectionState.Failed, 0, ex.Message);
        }
    }

    private static bool HasCredentialsFor(McpAuthType authType, ServerCredentials? creds) => authType switch
    {
        McpAuthType.Bearer => !string.IsNullOrEmpty(creds?.BearerToken),
        McpAuthType.ApiKey => creds?.ApiKey is { Key.Length: > 0, Token.Length: > 0 },
        McpAuthType.OAuth => !string.IsNullOrEmpty(creds?.OAuth?.AccessToken),
        _ => true,
    };

    private async Task EnumerateAndRegisterAsync(McpServerConfig server, CancellationToken ct)
    {
        var client = await _pool.GetAsync(server.Name, ct).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);

        var overrides = server.ToolOverrides
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tools)
        {
            // Single source of truth: compose, then validate the composed wire name.
            // Catches bad server names, bad tool names, and over-length composites in one shot.
            var composed = ToolNaming.Compose(server.Name, t.Name);
            if (!ToolNaming.IsValidWireName(composed))
            {
                _logger.LogWarning(
                    "Skipping MCP tool '{Server}.{Name}' — composed wire name would not be valid.",
                    server.Name, t.Name);
                continue;
            }

            var description = t.Description;
            if (overrides.TryGetValue(t.Name, out var ovr) && !string.IsNullOrEmpty(ovr.DescriptionOverride))
            {
                description = ovr.DescriptionOverride;
            }

            JsonObject parameters;
            try
            {
                parameters = SchemaToJsonObject(t.JsonSchema);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not parse JSON schema for MCP tool '{Server}.{Name}' — registering with empty schema.",
                    server.Name, t.Name);
                parameters = [];
            }

            var adapter = new McpToolAdapter(
                _pool,
                server.Name,
                t.Name,
                description ?? string.Empty,
                parameters,
                _loggerFactory.CreateLogger<McpToolAdapter>());

            _toolRegistry.Add(adapter);
            registered.Add(adapter.Name);
        }

        _toolsByServer[server.Name] = registered;
        _logger.LogInformation(
            "Registered {Count} tools from MCP server '{Server}'.", registered.Count, server.Name);
    }

    private async Task UnregisterServerToolsAsync(string serverName)
    {
        if (!_toolsByServer.TryRemove(serverName, out var names))
        {
            return;
        }
        foreach (var wireName in names)
        {
            _toolRegistry.Remove(wireName);
        }
        await _pool.EvictAsync(serverName).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static JsonObject SchemaToJsonObject(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Undefined || schema.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (JsonNode.Parse(schema.GetRawText()) is JsonObject obj)
        {
            return obj;
        }
        return [];
    }
}
