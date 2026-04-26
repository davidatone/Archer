using System.Text.RegularExpressions;

namespace Archer.Tools.Safety;

internal static partial class SecretRedactor
{
    [GeneratedRegex(
        @"(?i)(api[_-]?key|secret|bearer|password|token|connection ?string)\s*[:=]\s*[""']?[A-Za-z0-9_\-./+=]{12,}[""']?",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex PrivateKeyPattern();

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var s = SecretPattern().Replace(input, "[redacted-secret]");
        s = PrivateKeyPattern().Replace(s, "[redacted-private-key]");
        return s;
    }
}
