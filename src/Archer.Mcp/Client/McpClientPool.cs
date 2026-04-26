using System.Collections.Concurrent;
using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Archer.Mcp.Client;

/// <summary>
/// Default <see cref="IMcpClientPool"/>. Singleton lifetime. Connects once per server,
/// caches the connected <see cref="McpClient"/>, single-flights concurrent first-connects.
/// </summary>
public sealed class McpClientPool : IMcpClientPool
{
    private readonly IMcpServerRegistry _registry;
    private readonly ICredentialStore _credentials;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientPool> _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients =
        new(StringComparer.Ordinal);

    public McpClientPool(
        IMcpServerRegistry registry,
        ICredentialStore credentials,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(credentials);
        _registry = registry;
        _credentials = credentials;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<McpClientPool>();
    }

    public ValueTask<McpClient> GetAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);

        // The Lazy's factory is invoked at most once per server. Pass CancellationToken.None to
        // ConnectAsync so the connect is owned by the pool, not the first caller — otherwise a
        // cancellation by caller A poisons the cached Task forever and every later caller
        // observes a TaskCanceledException until someone explicitly evicts.
        var lazy = _clients.GetOrAdd(
            serverName,
            n => new Lazy<Task<McpClient>>(() => ConnectAsync(n, CancellationToken.None)));

        // Caller cancellation only affects this caller's await, not the shared underlying task.
        return new ValueTask<McpClient>(lazy.Value.WaitAsync(cancellationToken));
    }

    public async ValueTask EvictAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);

        if (!_clients.TryRemove(serverName, out var lazy))
        {
            return;
        }

        // The Lazy may not have run yet; if it has, dispose the resulting client.
        if (lazy.IsValueCreated)
        {
            try
            {
                var client = await lazy.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing evicted MCP client for {Server}", serverName);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, lazy) in _clients)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }
            try
            {
                var client = await lazy.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client during pool dispose");
            }
        }
        _clients.Clear();
    }

    private async Task<McpClient> ConnectAsync(string serverName, CancellationToken cancellationToken)
    {
        var config = _registry.Get(serverName)
            ?? throw new InvalidOperationException($"No MCP server named '{serverName}' is registered.");

        if (config.Disabled)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is disabled in configuration.");
        }

        var transport = await BuildTransportAsync(config, cancellationToken).ConfigureAwait(false);

        var clientOptions = new McpClientOptions
        {
            ClientInfo = new() { Name = "archer", Version = "0.1.0" },
            InitializationTimeout = TimeSpan.FromSeconds(config.Timeouts.ConnectSeconds),
        };

        _logger.LogInformation(
            "Connecting to MCP server '{Server}' at {Endpoint} (transport={Transport}, auth={Auth})",
            config.Name,
            config.Transport.Endpoint,
            config.Transport.Type,
            config.Auth.Type);

        var client = await McpClient.CreateAsync(
            transport,
            clientOptions,
            _loggerFactory,
            cancellationToken).ConfigureAwait(false);

        return client;
    }

    private async Task<IClientTransport> BuildTransportAsync(McpServerConfig config, CancellationToken ct)
    {
        var creds = await _credentials.GetAsync(config.Name, ct).ConfigureAwait(false);

        switch (config.Transport.Type)
        {
            case McpTransportType.Sse:
            case McpTransportType.StreamableHttp:
                return BuildHttpTransport(config, creds);

            case McpTransportType.Stdio:
                return BuildStdioTransport(config, creds);

            default:
                throw new InvalidOperationException($"Unknown transport type: {config.Transport.Type}");
        }
    }

    private HttpClientTransport BuildHttpTransport(McpServerConfig config, ServerCredentials? credentials)
    {
        if (config.Transport.Endpoint is null)
        {
            throw new InvalidOperationException(
                $"MCP server '{config.Name}' has no transport.endpoint configured.");
        }

        var warnings = new List<string>();
        var resolvedHeaders = new Dictionary<string, string>(
            EnvResolver.ResolveAll(config.Transport.Headers, credentials, warnings),
            StringComparer.OrdinalIgnoreCase);

        // Convenience: bearer auth implies the standard Authorization header. Skip if the
        // user already configured it explicitly via transport.headers.
        if (config.Auth.Type == McpAuthType.Bearer
            && !resolvedHeaders.ContainsKey("Authorization"))
        {
            if (credentials?.BearerToken is { } token && token.Length > 0)
            {
                resolvedHeaders["Authorization"] = "Bearer " + token;
            }
            else
            {
                _logger.LogWarning(
                    "MCP server '{Server}' is configured with auth.type=bearer but no token is in " +
                    "the credential store. Connection will proceed unauthenticated.", config.Name);
            }
        }

        LogResolverWarnings(config.Name, "transport.headers", warnings);

        var mode = config.Transport.Type switch
        {
            McpTransportType.Sse => HttpTransportMode.Sse,
            McpTransportType.StreamableHttp => HttpTransportMode.StreamableHttp,
            _ => HttpTransportMode.AutoDetect,
        };

        var options = new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = config.Transport.Endpoint,
            TransportMode = mode,
            ConnectionTimeout = TimeSpan.FromSeconds(config.Timeouts.ConnectSeconds),
            AdditionalHeaders = resolvedHeaders,
        };

        if (config.Auth.Type == McpAuthType.OAuth)
        {
            options.OAuth = OAuthOptionsFactory.Build(config, _credentials, credentials, _loggerFactory.CreateLogger("Archer.Mcp.Auth"));
        }

        return new HttpClientTransport(options, _loggerFactory);
    }

    private StdioClientTransport BuildStdioTransport(McpServerConfig config, ServerCredentials? credentials)
    {
        if (string.IsNullOrEmpty(config.Transport.Command))
        {
            throw new InvalidOperationException(
                $"MCP server '{config.Name}' uses stdio transport but transport.command is not set.");
        }

        var warnings = new List<string>();
        var resolvedEnv = EnvResolver.ResolveAll(config.Transport.Env, credentials, warnings);
        LogResolverWarnings(config.Name, "transport.env", warnings);

        var options = new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Transport.Command,
            Arguments = [.. config.Transport.Args],
            EnvironmentVariables = resolvedEnv.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            ShutdownTimeout = TimeSpan.FromSeconds(config.Timeouts.ConnectSeconds),
        };

        return new StdioClientTransport(options, _loggerFactory);
    }

    private void LogResolverWarnings(string serverName, string contextLabel, IReadOnlyList<string> warnings)
    {
        foreach (var w in warnings)
        {
            _logger.LogWarning(
                "MCP server '{Server}' {Context}: {Warning}",
                serverName, contextLabel, w);
        }
    }
}
