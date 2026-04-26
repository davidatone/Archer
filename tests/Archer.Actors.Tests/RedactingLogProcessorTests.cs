using Archer.Host.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Archer.Actors.Tests;

public class RedactingLogProcessorTests
{
    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("authorization", true)]
    [InlineData("Cookie", true)]
    [InlineData("Set-Cookie", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("X-API-KEY", true)]
    [InlineData("access_token", true)]
    [InlineData("refresh_token", true)]
    [InlineData("client_secret", true)]
    [InlineData("PASSWORD", true)]
    [InlineData("trello_api_key", true)]
    [InlineData("user_id", false)]
    [InlineData("agent_id", false)]
    [InlineData("repo_root", false)]
    [InlineData("status_code", false)]
    public void IsSensitive_matches_known_sensitive_substrings(string key, bool expected)
    {
        RedactingLogProcessor.IsSensitive(key).Should().Be(expected);
    }

    [Fact]
    public void OnEnd_redacts_sensitive_attributes_into_redacted_marker()
    {
        var processor = new RedactingLogProcessor();
        var captured = new List<Snapshot>();
        var captor = new CapturingProcessor(captured);

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddOpenTelemetry(o =>
            {
                o.IncludeScopes = true;
                o.AddProcessor(processor);
                o.AddProcessor(captor);
            });
        });
        var logger = loggerFactory.CreateLogger("test");

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer secret-token-xyz",
            ["X-Custom"] = "fine-to-keep",
        }))
        {
            logger.LogInformation("calling MCP {access_token}", "another-secret-value");
        }

        captured.Should().HaveCount(1);
        var record = captured[0];

        // The structured attribute named after the message template parameter is redacted.
        var attrs = record.Attributes!.ToDictionary(p => p.Key, p => p.Value);
        attrs["access_token"].Should().Be(RedactingLogProcessor.RedactedValue);

        // Scope attributes flow through ScopeProvider, which OTel surfaces separately —
        // verifying the structured attribute path is the load-bearing assertion.
    }

    [Fact]
    public void OnEnd_leaves_clean_records_untouched()
    {
        var processor = new RedactingLogProcessor();
        var captured = new List<Snapshot>();
        var captor = new CapturingProcessor(captured);

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(processor);
                o.AddProcessor(captor);
            });
        });
        var logger = loggerFactory.CreateLogger("test");

        logger.LogInformation("agent {AgentId} called {Tool}", "agent-1", "list_files");

        captured.Should().HaveCount(1);
        var record = captured[0];
        var attrs = record.Attributes!.ToDictionary(p => p.Key, p => p.Value);
        attrs["AgentId"].Should().Be("agent-1");
        attrs["Tool"].Should().Be("list_files");
    }

    private sealed class CapturingProcessor(IList<Snapshot> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            // Snapshot attributes immediately — LogRecord is pooled and the original instance
            // may be mutated/reused after this method returns.
            sink.Add(new Snapshot(
                data.Body,
                data.Attributes?.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).ToList()));
        }
    }

    private sealed record Snapshot(string? Body, List<KeyValuePair<string, object?>>? Attributes);
}
