using System.Text.RegularExpressions;
using Archer.Domain.Mcp;

namespace Archer.Mcp.Client;

/// <summary>
/// Expands placeholder strings in MCP server config values:
/// <list type="bullet">
///   <item><c>${env:VAR_NAME}</c> → process environment variable.</item>
///   <item><c>${cred:bearerToken}</c> → <see cref="ServerCredentials.BearerToken"/>.</item>
///   <item><c>${cred:apiKey.key}</c> → <see cref="ApiKeyPair.Key"/>.</item>
///   <item><c>${cred:apiKey.token}</c> → <see cref="ApiKeyPair.Token"/>.</item>
///   <item><c>${cred:oauth.accessToken}</c> → <see cref="OAuthTokens.AccessToken"/>.</item>
/// </list>
/// Unknown placeholders resolve to the empty string and are logged as a warning to the
/// caller via <see cref="Resolve(string, ServerCredentials?, IList{string})"/>'s
/// <c>warnings</c> output parameter.
/// </summary>
public static partial class EnvResolver
{
    [GeneratedRegex(@"\$\{(env|cred):([A-Za-z0-9_.\-]+)\}")]
    private static partial Regex PlaceholderRegex();

    public static string Resolve(string input, ServerCredentials? credentials, IList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(warnings);

        return PlaceholderRegex().Replace(input, m =>
        {
            var kind = m.Groups[1].Value;
            var path = m.Groups[2].Value;
            return kind switch
            {
                "env" => Environment.GetEnvironmentVariable(path) ?? Warn(warnings, $"environment variable '{path}' is not set"),
                "cred" => ResolveCred(path, credentials, warnings),
                _ => string.Empty,
            };
        });
    }

    public static IReadOnlyDictionary<string, string> ResolveAll(
        IReadOnlyDictionary<string, string> source,
        ServerCredentials? credentials,
        IList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            result[key] = Resolve(value, credentials, warnings);
        }
        return result;
    }

    private static string ResolveCred(string path, ServerCredentials? credentials, IList<string> warnings)
    {
        if (credentials is null)
        {
            return Warn(warnings, $"placeholder '${{cred:{path}}}' has no credentials available");
        }
        return path switch
        {
            "bearerToken" => credentials.BearerToken
                ?? Warn(warnings, "${cred:bearerToken} is not set"),
            "apiKey.key" => credentials.ApiKey?.Key
                ?? Warn(warnings, "${cred:apiKey.key} is not set"),
            "apiKey.token" => credentials.ApiKey?.Token
                ?? Warn(warnings, "${cred:apiKey.token} is not set"),
            "oauth.accessToken" => credentials.OAuth?.AccessToken
                ?? Warn(warnings, "${cred:oauth.accessToken} is not set"),
            _ => Warn(warnings, $"unknown credential field '{path}'"),
        };
    }

    private static string Warn(IList<string> warnings, string message)
    {
        warnings.Add(message);
        return string.Empty;
    }
}
