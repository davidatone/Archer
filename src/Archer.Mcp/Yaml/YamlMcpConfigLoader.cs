using Archer.Domain.Mcp;
using YamlDotNet.RepresentationModel;

namespace Archer.Mcp.Yaml;

/// <summary>
/// Parses an <see cref="McpServerConfig"/> from a YAML document. Hand-rolled (no auto-binding)
/// so missing-required-field errors surface at the offending key, not at deserialization
/// noise lines. Refuses any literal credential values in <c>auth.token</c>, <c>auth.key</c>,
/// or similar — secrets must live in the credential store, not the YAML.
/// </summary>
public static class YamlMcpConfigLoader
{
    public static McpServerConfig LoadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new StreamReader(path);
        return Load(reader, path) with { SourcePath = path };
    }

    public static McpServerConfig Load(TextReader reader, string sourceLabel = "<yaml>")
    {
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0)
        {
            throw new InvalidDataException($"{sourceLabel}: empty YAML document.");
        }

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{sourceLabel}: top-level node must be a mapping.");
        }

        var name = RequireString(root, "name");
        ValidateName(name, sourceLabel);

        return new McpServerConfig
        {
            Name = name,
            Description = TryGetString(root, "description") ?? string.Empty,
            Disabled = TryGetBool(root, "disabled") ?? false,
            Transport = ParseTransport(RequireMapping(root, "transport"), sourceLabel),
            Auth = TryGetMapping(root, "auth") is { } auth
                ? ParseAuth(auth, sourceLabel)
                : new McpAuthConfig { Type = McpAuthType.None },
            Timeouts = TryGetMapping(root, "timeouts") is { } t ? ParseTimeouts(t) : new McpTimeouts(),
            Retry = TryGetMapping(root, "retry") is { } r ? ParseRetry(r) : new McpRetryPolicy(),
            ToolOverrides = ParseToolOverrides(root, sourceLabel),
        };
    }

    public static string Serialize(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sb = new System.Text.StringBuilder();
        WriteHeader(sb, config);
        WriteTransport(sb, config.Transport);
        WriteAuth(sb, config.Auth);
        WriteTimeouts(sb, config.Timeouts);
        WriteRetry(sb, config.Retry);
        WriteToolOverrides(sb, config.ToolOverrides);
        return sb.ToString();
    }

    private static void WriteHeader(System.Text.StringBuilder sb, McpServerConfig config)
    {
        sb.AppendLine($"name: {config.Name}");
        if (!string.IsNullOrEmpty(config.Description))
        {
            sb.AppendLine($"description: {YamlScalar(config.Description)}");
        }
        if (config.Disabled)
        {
            sb.AppendLine("disabled: true");
        }
    }

    private static void WriteTransport(System.Text.StringBuilder sb, McpTransportConfig transport)
    {
        sb.AppendLine("transport:");
        sb.AppendLine($"  type: {TransportTypeToYaml(transport.Type)}");
        if (transport.Endpoint is not null) sb.AppendLine($"  endpoint: {transport.Endpoint}");
        if (transport.Command is not null) sb.AppendLine($"  command: {YamlScalar(transport.Command)}");
        AppendList(sb, "  args:", "    - ", transport.Args.Select(YamlScalar));
        AppendDict(sb, "  headers:", "    ", transport.Headers, YamlScalar);
        AppendDict(sb, "  env:", "    ", transport.Env, YamlScalar);
    }

    private static void WriteAuth(System.Text.StringBuilder sb, McpAuthConfig auth)
    {
        sb.AppendLine("auth:");
        sb.AppendLine($"  type: {AuthTypeToYaml(auth.Type)}");
        if (auth.AuthorizationServer is not null) sb.AppendLine($"  authorizationServer: {auth.AuthorizationServer}");
        AppendList(sb, "  scopes:", "    - ", auth.Scopes.Select(YamlScalar));
        if (!string.IsNullOrEmpty(auth.ClientId)) sb.AppendLine($"  clientId: {YamlScalar(auth.ClientId)}");
        if (auth.RedirectPort != 0) sb.AppendLine($"  redirectPort: {auth.RedirectPort}");
    }

    private static void WriteTimeouts(System.Text.StringBuilder sb, McpTimeouts t)
    {
        sb.AppendLine("timeouts:");
        sb.AppendLine($"  connectSeconds: {t.ConnectSeconds}");
        sb.AppendLine($"  callSeconds: {t.CallSeconds}");
    }

    private static void WriteRetry(System.Text.StringBuilder sb, McpRetryPolicy r)
    {
        sb.AppendLine("retry:");
        sb.AppendLine($"  maxAttempts: {r.MaxAttempts}");
        sb.AppendLine($"  initialDelayMs: {r.InitialDelayMs}");
        sb.AppendLine($"  backoff: {r.Backoff.ToString().ToLowerInvariant()}");
    }

    private static void WriteToolOverrides(System.Text.StringBuilder sb, IReadOnlyList<McpToolOverride> tools)
    {
        if (tools.Count == 0) return;
        sb.AppendLine("tools:");
        foreach (var t in tools)
        {
            sb.AppendLine($"  - name: {t.Name}");
            if (!string.IsNullOrEmpty(t.DescriptionOverride))
            {
                sb.AppendLine($"    descriptionOverride: {YamlScalar(t.DescriptionOverride)}");
            }
        }
    }

    private static void AppendList(System.Text.StringBuilder sb, string header, string itemPrefix, IEnumerable<string> items)
    {
        var materialized = items.ToList();
        if (materialized.Count == 0) return;
        sb.AppendLine(header);
        foreach (var item in materialized) sb.AppendLine(itemPrefix + item);
    }

    private static void AppendDict(System.Text.StringBuilder sb, string header, string indent, IReadOnlyDictionary<string, string> dict, Func<string, string> valueQuoter)
    {
        if (dict.Count == 0) return;
        sb.AppendLine(header);
        foreach (var (k, v) in dict) sb.AppendLine($"{indent}{k}: {valueQuoter(v)}");
    }

    // ---- parsing -----------------------------------------------------------------------

    private static McpTransportConfig ParseTransport(YamlMappingNode node, string sourceLabel)
    {
        var typeStr = RequireString(node, "type");
        var type = typeStr.ToLowerInvariant() switch
        {
            "sse" => McpTransportType.Sse,
            "streamable-http" or "streamablehttp" or "http" => McpTransportType.StreamableHttp,
            "stdio" => McpTransportType.Stdio,
            _ => throw new InvalidDataException($"{sourceLabel}: unknown transport type '{typeStr}'."),
        };

        var endpointStr = TryGetString(node, "endpoint");
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(endpointStr) && !Uri.TryCreate(endpointStr, UriKind.Absolute, out endpoint))
        {
            throw new InvalidDataException($"{sourceLabel}: transport.endpoint is not a valid absolute URI: {endpointStr}");
        }

        return new McpTransportConfig
        {
            Type = type,
            Endpoint = endpoint,
            Headers = TryGetStringMap(node, "headers"),
            Command = TryGetString(node, "command"),
            Args = TryGetStringList(node, "args"),
            Env = TryGetStringMap(node, "env"),
        };
    }

    private static McpAuthConfig ParseAuth(YamlMappingNode node, string sourceLabel)
    {
        RejectLiteralSecret(node, "token", sourceLabel);
        RejectLiteralSecret(node, "clientSecret", sourceLabel);
        RejectLiteralSecret(node, "key", sourceLabel);
        RejectLiteralSecret(node, "apiKey", sourceLabel);

        var typeStr = RequireString(node, "type");
        var type = typeStr.ToLowerInvariant() switch
        {
            "none" => McpAuthType.None,
            "bearer" => McpAuthType.Bearer,
            "api-key" or "apikey" => McpAuthType.ApiKey,
            "oauth" or "oauth2" or "oauth2.1" => McpAuthType.OAuth,
            _ => throw new InvalidDataException($"{sourceLabel}: unknown auth type '{typeStr}'."),
        };

        Uri? authServer = null;
        var authServerStr = TryGetString(node, "authorizationServer");
        if (!string.IsNullOrWhiteSpace(authServerStr) && !Uri.TryCreate(authServerStr, UriKind.Absolute, out authServer))
        {
            throw new InvalidDataException($"{sourceLabel}: auth.authorizationServer is not a valid absolute URI.");
        }

        return new McpAuthConfig
        {
            Type = type,
            AuthorizationServer = authServer,
            Scopes = TryGetStringList(node, "scopes"),
            ClientId = TryGetString(node, "clientId"),
            RedirectPort = TryGetInt(node, "redirectPort") ?? 0,
        };
    }

    private static McpTimeouts ParseTimeouts(YamlMappingNode node) => new()
    {
        ConnectSeconds = TryGetInt(node, "connectSeconds") ?? 30,
        CallSeconds = TryGetInt(node, "callSeconds") ?? 60,
    };

    private static McpRetryPolicy ParseRetry(YamlMappingNode node)
    {
        var backoffStr = TryGetString(node, "backoff");
        var backoff = backoffStr?.ToLowerInvariant() switch
        {
            null or "" or "exponential" => McpRetryBackoff.Exponential,
            "constant" => McpRetryBackoff.Constant,
            _ => throw new InvalidDataException($"Unknown retry.backoff '{backoffStr}'."),
        };
        return new McpRetryPolicy
        {
            MaxAttempts = TryGetInt(node, "maxAttempts") ?? 3,
            InitialDelayMs = TryGetInt(node, "initialDelayMs") ?? 500,
            Backoff = backoff,
        };
    }

    private static List<McpToolOverride> ParseToolOverrides(YamlMappingNode root, string sourceLabel)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("tools"), out var node))
        {
            return [];
        }
        if (node is not YamlSequenceNode seq)
        {
            throw new InvalidDataException($"{sourceLabel}: 'tools' must be a sequence of mappings.");
        }
        var list = new List<McpToolOverride>();
        foreach (var item in seq)
        {
            if (item is not YamlMappingNode m)
            {
                throw new InvalidDataException($"{sourceLabel}: each entry under 'tools' must be a mapping.");
            }
            list.Add(new McpToolOverride
            {
                Name = RequireString(m, "name"),
                DescriptionOverride = TryGetString(m, "descriptionOverride"),
            });
        }
        return list;
    }

    // ---- helpers -----------------------------------------------------------------------

    private static void ValidateName(string name, string sourceLabel)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException($"{sourceLabel}: 'name' is required.");
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= '0' && c <= '9')
                  || c == '-' || c == '_';
            if (!ok)
            {
                throw new InvalidDataException(
                    $"{sourceLabel}: 'name' must match [a-z0-9_-]+ (got '{name}'). " +
                    "Reserved chars '.' and '__' are how tool names compose.");
            }
        }
    }

    private static void RejectLiteralSecret(YamlMappingNode node, string key, string sourceLabel)
    {
        if (TryGetString(node, key) is { } v && !v.StartsWith("${env:", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{sourceLabel}: literal value found in '{key}'. Secrets must not live in YAML — " +
                "use the credential store (or '${env:VAR_NAME}' for build-time injection).");
        }
    }

    /// <summary>Canonical YAML string for an <see cref="McpTransportType"/>. Same form
    /// used in the loader, the CLI display, and the TUI.</summary>
    public static string TransportTypeToYaml(McpTransportType t) => t switch
    {
        McpTransportType.Sse => "sse",
        McpTransportType.StreamableHttp => "streamable-http",
        McpTransportType.Stdio => "stdio",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Canonical YAML string for an <see cref="McpAuthType"/>.</summary>
    public static string AuthTypeToYaml(McpAuthType t) => t switch
    {
        McpAuthType.None => "none",
        McpAuthType.Bearer => "bearer",
        McpAuthType.ApiKey => "api-key",
        McpAuthType.OAuth => "oauth",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static string YamlScalar(string s)
    {
        // Quote anything that has YAML-special characters. Keeps the loader honest.
        var needsQuotes = s.Length == 0
            || s.IndexOfAny([':', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', '\n', '\r', '\t']) >= 0
            || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]);
        return needsQuotes ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : s;
    }

    private static string RequireString(YamlMappingNode node, string key) =>
        TryGetString(node, key) ?? throw new InvalidDataException($"Missing required field '{key}'.");

    private static string? TryGetString(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s
            ? s.Value
            : null;

    private static int? TryGetInt(YamlMappingNode node, string key)
        => TryGetString(node, key) is { } s && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    private static bool? TryGetBool(YamlMappingNode node, string key)
        => TryGetString(node, key) is { } s && bool.TryParse(s, out var b)
            ? b
            : null;

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key)
        => TryGetMapping(node, key) ?? throw new InvalidDataException($"Missing required mapping '{key}'.");

    private static YamlMappingNode? TryGetMapping(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlMappingNode : null;

    private static IReadOnlyList<string> TryGetStringList(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq)
        {
            return [];
        }
        return [.. seq.OfType<YamlScalarNode>().Select(s => s.Value!).Where(s => !string.IsNullOrWhiteSpace(s))];
    }

    private static Dictionary<string, string> TryGetStringMap(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlMappingNode map)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, val) in map.Children)
        {
            if (k is YamlScalarNode ks && val is YamlScalarNode vs && ks.Value is not null && vs.Value is not null)
            {
                result[ks.Value] = vs.Value;
            }
        }
        return result;
    }
}
