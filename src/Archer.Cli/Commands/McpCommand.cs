using System.CommandLine;
using Archer.Application.Mcp;
using Archer.Application.Tools;
using Archer.Cli.Hosting;
using Archer.Domain.Mcp;
using Archer.Mcp.Client;
using Archer.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Cli.Commands;

/// <summary>
/// <c>archer mcp …</c> — manage MCP server configurations and credentials.
/// Secrets are read from stdin by default so they don't leak into process listings
/// or shell history. CLI plumbing — System.CommandLine handlers wired to
/// <see cref="Console"/> + DI services. Excluded from coverage; the underlying services
/// are tested in their own assemblies.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class McpCommand
{
    private const string TransportStdio = "stdio";
    private const string ServerNameDescription = "MCP server name.";
    private const string EndpointNone = "(none)";

    public static Command Build()
    {
        var cmd = new Command("mcp", "Manage MCP server configurations and credentials.")
        {
            BuildList(),
            BuildAdd(),
            BuildTest(),
            BuildRemove(),
            BuildLogin(),
            BuildLogout(),
            BuildCredentials(),
        };
        return cmd;
    }

    // -- archer mcp add <name> --------------------------------------------------------

    private static Command BuildAdd()
    {
        var nameArg = new Argument<string>("name", "Server name (lowercase, [a-z0-9_-]+).");
        var transportOpt = new Option<string?>("--transport", "Transport type: stdio | streamable-http | sse.");
        var endpointOpt = new Option<string?>("--endpoint", "HTTP endpoint URL (for streamable-http / sse).");
        var commandOpt = new Option<string?>("--command", "Executable to spawn (for stdio).");
        var argsOpt = new Option<string?>("--args", "Comma-separated args to pass to the stdio command.");
        var authOpt = new Option<string?>("--auth", "Auth type: none | bearer | api-key | oauth.") { };
        var scopesOpt = new Option<string?>("--scopes", "OAuth scopes (space-separated).");
        var clientIdOpt = new Option<string?>("--client-id", "Pre-registered OAuth client id (skips DCR).");
        var authServerOpt = new Option<string?>("--auth-server", "Pin the OAuth authorization server URL.");
        var descriptionOpt = new Option<string?>("--description", "Human-readable description.");

        var cmd = new Command("add", "Register a new MCP server. Writes a YAML to the user-level mcp/ directory.")
        {
            nameArg, transportOpt, endpointOpt, commandOpt, argsOpt,
            authOpt, scopesOpt, clientIdOpt, authServerOpt, descriptionOpt,
            CommonOptions.StateDir,
        };

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ictx) =>
        {
            var input = new AddInput
            {
                Name = ictx.ParseResult.GetValueForArgument(nameArg),
                Transport = ictx.ParseResult.GetValueForOption(transportOpt),
                Endpoint = ictx.ParseResult.GetValueForOption(endpointOpt),
                Command = ictx.ParseResult.GetValueForOption(commandOpt),
                Args = ictx.ParseResult.GetValueForOption(argsOpt),
                Auth = ictx.ParseResult.GetValueForOption(authOpt),
                Scopes = ictx.ParseResult.GetValueForOption(scopesOpt),
                ClientId = ictx.ParseResult.GetValueForOption(clientIdOpt),
                AuthServer = ictx.ParseResult.GetValueForOption(authServerOpt),
                Description = ictx.ParseResult.GetValueForOption(descriptionOpt),
            };
            var state = ictx.ParseResult.GetValueForOption(CommonOptions.StateDir);

            ictx.ExitCode = await CliHost.RunAsync(
                (sp, ct) => RunAddAsync(sp, input, ct),
                configure: CommonOptions.StateDirOverride(state));
        });
        return cmd;
    }

    private static async Task<int> RunAddAsync(IServiceProvider sp, AddInput input, CancellationToken ct)
    {
        PromptForMissingFields(input);

        McpServerConfig config;
        try
        {
            config = BuildConfig(input);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Invalid configuration: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var registry = sp.GetRequiredService<IMcpServerRegistry>();
        if (registry.Get(input.Name) is not null)
        {
            await Console.Error.WriteLineAsync($"An MCP server named '{input.Name}' is already registered. Remove it first or pick another name.").ConfigureAwait(false);
            return 2;
        }

        await registry.AddOrUpdateAsync(config, ct).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Added MCP server '{input.Name}' ({input.Transport}, auth={input.Auth}).").ConfigureAwait(false);
        await PrintNextStepAsync(input.Name, input.Auth!).ConfigureAwait(false);
        return 0;
    }

    private static void PromptForMissingFields(AddInput input)
    {
        input.Transport ??= Prompt("transport", TransportStdio, [TransportStdio, "streamable-http", "sse"]);
        if (input.Transport == TransportStdio)
        {
            input.Command ??= Prompt("command (executable)", required: true);
        }
        else
        {
            input.Endpoint ??= Prompt("endpoint URL", required: true);
        }
        input.Auth ??= Prompt("auth", "none", ["none", "bearer", "api-key", "oauth"]);
    }

    private static async Task PrintNextStepAsync(string serverName, string auth)
    {
        switch (auth)
        {
            case "oauth":
                await Console.Out.WriteLineAsync($"Next: run `archer mcp login {serverName}` to complete the OAuth flow.").ConfigureAwait(false);
                break;
            case "bearer":
            case "api-key":
                await Console.Out.WriteLineAsync($"Next: run `archer mcp credentials set {serverName} --{auth}` to save credentials.").ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Mutable bag for the <c>mcp add</c> handler's resolved arguments.</summary>
    private sealed class AddInput
    {
        public required string Name { get; init; }
        public string? Transport { get; set; }
        public string? Endpoint { get; set; }
        public string? Command { get; set; }
        public string? Args { get; init; }
        public string? Auth { get; set; }
        public string? Scopes { get; init; }
        public string? ClientId { get; init; }
        public string? AuthServer { get; init; }
        public string? Description { get; init; }
    }

    private static McpServerConfig BuildConfig(AddInput input)
    {
        var transportType = ParseTransportType(input.Transport!);
        var authType = ParseAuthType(input.Auth!);
        var endpointUri = ParseAbsoluteUriOrNull(input.Endpoint, "Endpoint");
        var authServerUri = ParseAbsoluteUriOrNull(input.AuthServer, "--auth-server");
        var argList = SplitOrEmpty(input.Args, ',');
        var scopeList = SplitOrEmpty(input.Scopes, ' ');

        return new McpServerConfig
        {
            Name = input.Name,
            Description = input.Description ?? string.Empty,
            Transport = new McpTransportConfig
            {
                Type = transportType,
                Endpoint = endpointUri,
                Command = input.Command,
                Args = argList,
            },
            Auth = new McpAuthConfig
            {
                Type = authType,
                Scopes = scopeList,
                ClientId = input.ClientId,
                AuthorizationServer = authServerUri,
            },
        };
    }

    private static McpTransportType ParseTransportType(string transport) => transport.ToLowerInvariant() switch
    {
        "stdio" => McpTransportType.Stdio,
        "streamable-http" or "http" => McpTransportType.StreamableHttp,
        "sse" => McpTransportType.Sse,
        _ => throw new ArgumentException($"Unknown transport '{transport}'."),
    };

    private static McpAuthType ParseAuthType(string auth) => auth.ToLowerInvariant() switch
    {
        "none" => McpAuthType.None,
        "bearer" => McpAuthType.Bearer,
        "api-key" => McpAuthType.ApiKey,
        "oauth" => McpAuthType.OAuth,
        _ => throw new ArgumentException($"Unknown auth '{auth}'."),
    };

    private static Uri? ParseAbsoluteUriOrNull(string? raw, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"{fieldName} '{raw}' is not a valid absolute URI.");
        }
        return uri;
    }

    private static string[] SplitOrEmpty(string? source, char separator)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        return source.Split(separator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Prompt(string label, string? @default = null, string[]? choices = null, bool required = false)
    {
        Console.Error.Write(label);
        if (choices is not null) Console.Error.Write($" [{string.Join("|", choices)}]");
        if (@default is not null) Console.Error.Write($" (default: {@default})");
        Console.Error.Write(": ");
        var input = Console.In.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            if (@default is not null) return @default;
            if (required) throw new ArgumentException($"{label} is required.");
            return string.Empty;
        }
        return input;
    }

    // -- archer mcp login <name> ------------------------------------------------------

    private static Command BuildLogin()
    {
        var nameArg = new Argument<string>("name", "MCP server name (must use auth.type: oauth).");
        var cmd = new Command("login", "Run the OAuth 2.1 + PKCE flow for an MCP server. Opens a browser and persists tokens.")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var registry = sp.GetRequiredService<IMcpServerRegistry>();
                var server = registry.Get(name);
                if (server is null)
                {
                    await Console.Error.WriteLineAsync($"No MCP server named '{name}' is registered.").ConfigureAwait(false);
                    return 2;
                }
                if (server.Auth.Type != McpAuthType.OAuth)
                {
                    Console.Error.WriteLine(
                        $"Server '{name}' uses auth.type={Archer.Mcp.Yaml.YamlMcpConfigLoader.AuthTypeToYaml(server.Auth.Type)}. " +
                        "`mcp login` is only for auth.type=oauth. Use `mcp credentials set` for bearer/api-key.");
                    return 2;
                }

                await Console.Out.WriteLineAsync($"Starting OAuth flow for '{name}'…").ConfigureAwait(false);

                // Force a fresh dance: clear any cached session in the pool. Existing tokens
                // (if still valid) would otherwise be reused without a browser.
                var pool = sp.GetRequiredService<IMcpClientPool>();
                await pool.EvictAsync(name, ct).ConfigureAwait(false);

                try
                {
                    var client = await pool.GetAsync(name, ct).ConfigureAwait(false);
                    var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                    await Console.Out.WriteLineAsync($"Logged in to '{name}'. {tools.Count} tool(s).ConfigureAwait(false) available.");

                    if (sp.GetService<McpToolSource>() is { } source)
                    {
                        await source.RefreshAsync(name, ct).ConfigureAwait(false);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Login failed: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    // -- archer mcp logout <name> -----------------------------------------------------

    private static Command BuildLogout()
    {
        var nameArg = new Argument<string>("name", ServerNameDescription);
        var cmd = new Command("logout", "Forget tokens for a server and drop its cached connection.")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var store = sp.GetRequiredService<ICredentialStore>();
                var pool = sp.GetRequiredService<IMcpClientPool>();
                var removed = await store.DeleteAsync(name, ct).ConfigureAwait(false);
                await pool.EvictAsync(name, ct).ConfigureAwait(false);

                if (sp.GetService<McpToolSource>() is { } source)
                {
                    await source.RefreshAsync(name, ct).ConfigureAwait(false);
                }
                Console.WriteLine(removed
                    ? $"Logged out of '{name}'."
                    : $"No credentials stored for '{name}'.");
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    // -- archer mcp list --------------------------------------------------------------

    private static Command BuildList()
    {
        var cmd = new Command("list", "List configured MCP servers and credential status.")
        {
            CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string? state) =>
        {
            await CliHost.RunAsync(RunListAsync, configure: CommonOptions.StateDirOverride(state));
        }, CommonOptions.StateDir);
        return cmd;
    }

    private static async Task<int> RunListAsync(IServiceProvider sp, CancellationToken ct)
    {
        var registry = sp.GetRequiredService<IMcpServerRegistry>();
        var creds = sp.GetRequiredService<ICredentialStore>();
        var servers = registry.All.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        if (servers.Count == 0)
        {
            await Console.Out.WriteLineAsync("(no MCP servers registered — drop a YAML in ./mcp/ or run `archer mcp add <name>`).ConfigureAwait(false)");
            return 0;
        }

        await Console.Out.WriteLineAsync($"{"NAME",-20} {"TRANSPORT",-15} {"AUTH",-10} {"CREDS",-8} ENDPOINT/COMMAND").ConfigureAwait(false);
        foreach (var s in servers)
        {
            var hasCreds = await creds.GetAsync(s.Name, ct).ConfigureAwait(false) is not null;
            await Console.Out.WriteLineAsync(FormatListRow(s, hasCreds)).ConfigureAwait(false);
        }
        return 0;
    }

    private static string FormatListRow(McpServerConfig s, bool hasCreds)
    {
        var endpoint = s.Transport.Endpoint?.ToString()
            ?? (s.Transport.Command is { Length: > 0 }
                ? $"{s.Transport.Command} {string.Join(' ', s.Transport.Args)}"
                : EndpointNone);
        var disabled = s.Disabled ? " (DISABLED)" : "";
        var transport = Archer.Mcp.Yaml.YamlMcpConfigLoader.TransportTypeToYaml(s.Transport.Type);
        var auth = Archer.Mcp.Yaml.YamlMcpConfigLoader.AuthTypeToYaml(s.Auth.Type);
        return $"{s.Name,-20} {transport,-15} {auth,-10} {(hasCreds ? "✓" : "—"),-8} {endpoint}{disabled}";
    }

    // -- archer mcp test <name> -------------------------------------------------------

    private static Command BuildTest()
    {
        var nameArg = new Argument<string>("name", ServerNameDescription);
        var cmd = new Command("test", "Connect to a server and enumerate its tools.")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var registry = sp.GetRequiredService<IMcpServerRegistry>();
                if (registry.Get(name) is null)
                {
                    await Console.Error.WriteLineAsync($"No MCP server named '{name}' is registered.").ConfigureAwait(false);
                    return 2;
                }

                var pool = sp.GetRequiredService<IMcpClientPool>();
                await Console.Out.WriteLineAsync($"Connecting to '{name}'…").ConfigureAwait(false);
                try
                {
                    var client = await pool.GetAsync(name, ct).ConfigureAwait(false);
                    var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                    await Console.Out.WriteLineAsync($"Connected. {tools.Count} tool(s).ConfigureAwait(false):");
                    foreach (var t in tools)
                    {
                        var wireName = ToolNaming.Compose(name, t.Name);
                        var description = (t.Description ?? "").Replace('\n', ' ').Trim();
                        if (description.Length > 80) description = description[..77] + "…";
                        await Console.Out.WriteLineAsync($"  {wireName,-40} {description}").ConfigureAwait(false);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Connection failed: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    // -- archer mcp remove <name> -----------------------------------------------------

    private static Command BuildRemove()
    {
        var nameArg = new Argument<string>("name", "MCP server name to remove.");
        var cmd = new Command("remove", "Remove a user-level MCP server config (does not touch repo-level YAMLs).")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var registry = sp.GetRequiredService<IMcpServerRegistry>();
                var removed = await registry.RemoveAsync(name, ct).ConfigureAwait(false);
                if (!removed)
                {
                    Console.Error.WriteLine(
                        $"No user-level config for '{name}' to remove. (Repo-level YAMLs aren't deleted by this command.)");
                    return 2;
                }
                await Console.Out.WriteLineAsync($"Removed user-level config for '{name}'.").ConfigureAwait(false);
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    // -- archer mcp credentials … -----------------------------------------------------

    private static Command BuildCredentials()
    {
        var cmd = new Command("credentials", "Manage encrypted credentials for MCP servers.")
        {
            BuildCredentialsSet(),
            BuildCredentialsShow(),
            BuildCredentialsDelete(),
        };
        return cmd;
    }

    private static Command BuildCredentialsSet()
    {
        var nameArg = new Argument<string>("name", ServerNameDescription);
        var bearerOpt = new Option<bool>("--bearer", "Set a bearer token. Value read from stdin (one line).");
        var apiKeyOpt = new Option<bool>("--api-key", "Set a Trello-style API key + token pair. Both read from stdin (key on line 1, token on line 2).");

        var cmd = new Command("set", "Save credentials for a server. Secrets are read from stdin so they don't leak into process listings.")
        {
            nameArg, bearerOpt, apiKeyOpt, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, bool bearer, bool apiKey, string? state) =>
        {
            await CliHost.RunAsync(
                (sp, ct) => RunCredentialsSetAsync(sp, name, bearer, apiKey, ct),
                configure: CommonOptions.StateDirOverride(state));
        }, nameArg, bearerOpt, apiKeyOpt, CommonOptions.StateDir);
        return cmd;
    }

    private static async Task<int> RunCredentialsSetAsync(
        IServiceProvider sp, string name, bool bearer, bool apiKey, CancellationToken ct)
    {
        if (bearer == apiKey)
        {
            await Console.Error.WriteLineAsync("Specify exactly one of --bearer or --api-key.").ConfigureAwait(false);
            return 2;
        }

        var creds = bearer ? await ReadBearerCredentialAsync() : await ReadApiKeyCredentialAsync();
        if (creds is null) return 2;

        var store = sp.GetRequiredService<ICredentialStore>();
        await store.SaveAsync(name, creds, ct).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Saved credentials for '{name}'.").ConfigureAwait(false);

        // Trigger a re-enumeration so the running silo picks up the new credentials
        // without a restart. No-op if the server isn't registered.
        if (sp.GetService<McpToolSource>() is { } source)
        {
            await source.RefreshAsync(name, ct).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<ServerCredentials?> ReadBearerCredentialAsync()
    {
        await Console.Error.WriteAsync("token: ").ConfigureAwait(false);
        var token = await ReadSecretLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            await Console.Error.WriteLineAsync("(empty token — aborting).ConfigureAwait(false)");
            return null;
        }
        return new ServerCredentials { BearerToken = token };
    }

    private static async Task<ServerCredentials?> ReadApiKeyCredentialAsync()
    {
        await Console.Error.WriteAsync("api key: ").ConfigureAwait(false);
        var key = await ReadSecretLineAsync().ConfigureAwait(false);
        await Console.Error.WriteAsync("token:   ").ConfigureAwait(false);
        var tok = await ReadSecretLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(tok))
        {
            await Console.Error.WriteLineAsync("(empty value — aborting).ConfigureAwait(false)");
            return null;
        }
        return new ServerCredentials { ApiKey = new ApiKeyPair { Key = key, Token = tok } };
    }

    private static Command BuildCredentialsShow()
    {
        var nameArg = new Argument<string>("name", ServerNameDescription);
        var cmd = new Command("show", "Show what kind of credential is stored (never the value).")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var store = sp.GetRequiredService<ICredentialStore>();
                var creds = await store.GetAsync(name, ct).ConfigureAwait(false);
                if (creds is null)
                {
                    await Console.Out.WriteLineAsync($"No credentials stored for '{name}'.").ConfigureAwait(false);
                    return 0;
                }
                await Console.Out.WriteLineAsync($"Server:        {name}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Saved (UTC):   {creds.SavedAtUtc:O}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Bearer token:  {(string.IsNullOrEmpty(creds.BearerToken) ? EndpointNone : "(set)")}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"API key+token: {(creds.ApiKey is null ? EndpointNone : "(both set)")}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"OAuth tokens:  {(creds.OAuth is null ? EndpointNone : "(set)")}").ConfigureAwait(false);
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    private static Command BuildCredentialsDelete()
    {
        var nameArg = new Argument<string>("name", ServerNameDescription);
        var cmd = new Command("delete", "Remove credentials for a server.")
        {
            nameArg, CommonOptions.StateDir,
        };
        cmd.SetHandler(async (string name, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var store = sp.GetRequiredService<ICredentialStore>();
                var removed = await store.DeleteAsync(name, ct).ConfigureAwait(false);
                Console.WriteLine(removed
                    ? $"Removed credentials for '{name}'."
                    : $"No credentials stored for '{name}'.");
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, nameArg, CommonOptions.StateDir);
        return cmd;
    }

    // -- helpers ----------------------------------------------------------------------

    /// <summary>
    /// Read one line from stdin without echoing if the input is an interactive terminal.
    /// If stdin is redirected (e.g. piped from a file or another process), read normally —
    /// the caller's responsible for the security of their pipe.
    /// </summary>
    private static async Task<string?> ReadSecretLineAsync()
    {
        if (!Console.IsInputRedirected)
        {
            // Interactive: silence echo so the secret doesn't appear on screen.
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    await Console.Error.WriteLineAsync().ConfigureAwait(false);
                    break;
                }
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Length -= 1;
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }
            return sb.ToString();
        }

        return await Console.In.ReadLineAsync().ConfigureAwait(false);
    }
}
