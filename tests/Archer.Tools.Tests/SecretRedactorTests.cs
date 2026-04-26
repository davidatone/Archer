using Archer.Tools.Safety;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void Redact_returns_empty_for_empty_input()
    {
        SecretRedactor.Redact("").Should().BeEmpty();
    }

    [Fact]
    public void Redact_returns_input_unchanged_when_nothing_matches()
    {
        const string input = "the quick brown fox";
        SecretRedactor.Redact(input).Should().Be(input);
    }

    [Theory]
    [InlineData("api_key=ABCDEFGH1234567890")]
    [InlineData("apikey=ABCDEFGH1234567890")]
    [InlineData("api-key=ABCDEFGH1234567890")]
    [InlineData("secret=ABCDEFGH1234567890")]
    [InlineData("token=ABCDEFGH1234567890")]
    [InlineData("password=hunter22helloworld")]
    [InlineData("bearer=ABCDEFGH1234567890")]
    [InlineData("connection string=ABCDEFGHIJKLMNOP1234")]
    public void Redact_replaces_known_secret_patterns(string secret)
    {
        var redacted = SecretRedactor.Redact($"prefix {secret} suffix");
        redacted.Should().Contain("[redacted-secret]");
        redacted.Should().NotContain(secret.Split('=')[1]);
    }

    [Fact]
    public void Redact_collapses_inline_PEM_private_key()
    {
        var key = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----";
        var redacted = SecretRedactor.Redact(key);
        redacted.Should().Be("[redacted-private-key]");
    }

    [Fact]
    public void Redact_handles_openssh_private_key_envelope()
    {
        var key = "-----BEGIN OPENSSH PRIVATE KEY-----\ndata\n-----END OPENSSH PRIVATE KEY-----";
        SecretRedactor.Redact(key).Should().Be("[redacted-private-key]");
    }

    [Fact]
    public void Redact_is_case_insensitive_for_keywords()
    {
        SecretRedactor.Redact("API_KEY=ABCDEFGH1234567890")
            .Should().Contain("[redacted-secret]");
    }

    [Fact]
    public void Redact_does_not_match_short_value_below_minimum_length()
    {
        // The pattern requires at least 12 chars after the separator.
        const string input = "secret=short";
        SecretRedactor.Redact(input).Should().Be(input);
    }
}
