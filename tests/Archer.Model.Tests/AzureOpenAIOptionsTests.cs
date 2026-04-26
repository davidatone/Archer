using Archer.Domain.Agents;
using Archer.Model;
using FluentAssertions;

namespace Archer.Model.Tests;

public class AzureOpenAIOptionsTests
{
    [Fact]
    public void Defaults_match_documented_values()
    {
        var opts = new AzureOpenAIOptions();
        opts.DefaultDeployment.Should().Be("gpt-5-mini");
        opts.UseV1Surface.Should().BeTrue();
        opts.MaxCompletionTokens.Should().Be(16384);
        opts.ReasoningEffort.Should().Be(ReasoningEffort.Medium);
        opts.ReasoningSummary.Should().Be(ReasoningSummary.Auto);
        opts.Endpoint.Should().BeNull();
        opts.ApiKey.Should().BeNull();
        opts.ApiVersion.Should().BeNull();
    }

    [Fact]
    public void Properties_are_settable()
    {
        var opts = new AzureOpenAIOptions
        {
            Endpoint = "https://example.openai.azure.com",
            ApiKey = "k",
            ApiVersion = "2025-04-01-preview",
            DefaultDeployment = "gpt-x",
            UseV1Surface = false,
            MaxCompletionTokens = 8192,
            ReasoningEffort = ReasoningEffort.Low,
            ReasoningSummary = ReasoningSummary.Concise,
        };
        opts.Endpoint.Should().Contain("example");
        opts.ApiKey.Should().Be("k");
        opts.UseV1Surface.Should().BeFalse();
        opts.MaxCompletionTokens.Should().Be(8192);
        opts.ReasoningEffort.Should().Be(ReasoningEffort.Low);
    }
}
