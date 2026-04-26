using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Archer.Domain.Agents;

public static partial class AgentId
{
    private const string Prefix = "agent_";
    private const int RandomLen = 12;

    // Accept either the new `agent_` prefix or the legacy `scout_` prefix so any state
    // files or events.ndjson written before the rename still load. Source-generated +
    // bounded-time so a hostile or malformed input can't trigger ReDoS.
    [GeneratedRegex(@"^(agent|scout)_[A-Z0-9]{12}$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AgentIdPattern();

    public static string New()
    {
        Span<char> buffer = stackalloc char[RandomLen];
        ReadOnlySpan<char> alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";
        Span<byte> bytes = stackalloc byte[RandomLen];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < RandomLen; i++)
        {
            buffer[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return Prefix + new string(buffer);
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && AgentIdPattern().IsMatch(value);
}
