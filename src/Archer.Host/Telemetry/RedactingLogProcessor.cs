using System.Collections.Generic;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Archer.Host.Telemetry;

/// <summary>
/// Strips known-sensitive attributes from <see cref="LogRecord"/>s before they reach an exporter.
/// Catches:
/// <list type="bullet">
///   <item>HTTP auth headers (<c>Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c>, <c>Proxy-Authorization</c>, <c>X-Api-Key</c>, <c>X-Auth-Token</c>).</item>
///   <item>OAuth payload fields (<c>access_token</c>, <c>refresh_token</c>, <c>id_token</c>, <c>code</c>, <c>client_secret</c>).</item>
///   <item>Generic credential field names (<c>password</c>, <c>secret</c>, <c>token</c>, <c>api_key</c>).</item>
/// </list>
/// Matches case-insensitive contains. Replaces values with <c>&lt;redacted&gt;</c>.
/// </summary>
public sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
{
    private static readonly string[] SensitiveSubstrings =
    [
        "authorization",
        "cookie",
        "x-api-key",
        "x-auth-token",
        "proxy-authorization",
        "access_token",
        "refresh_token",
        "id_token",
        "client_secret",
        "password",
        "secret",
        "api_key",
        "apikey",
    ];

    public const string RedactedValue = "<redacted>";

    public override void OnEnd(LogRecord data)
    {
        if (data.Attributes is null || data.Attributes.Count == 0)
        {
            return;
        }

        // Decide whether this record needs scrubbing (avoids allocating a new list when clean).
        var needsScrub = false;
        for (var i = 0; i < data.Attributes.Count; i++)
        {
            if (IsSensitive(data.Attributes[i].Key))
            {
                needsScrub = true;
                break;
            }
        }
        if (!needsScrub)
        {
            return;
        }

        var scrubbed = new List<KeyValuePair<string, object?>>(data.Attributes.Count);
        for (var i = 0; i < data.Attributes.Count; i++)
        {
            var pair = data.Attributes[i];
            scrubbed.Add(IsSensitive(pair.Key)
                ? new KeyValuePair<string, object?>(pair.Key, RedactedValue)
                : pair);
        }
        data.Attributes = scrubbed;
    }

    public static bool IsSensitive(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        for (var i = 0; i < SensitiveSubstrings.Length; i++)
        {
            if (key.Contains(SensitiveSubstrings[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
