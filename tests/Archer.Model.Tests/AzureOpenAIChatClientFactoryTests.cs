using Archer.Model;
using Archer.Model.AgentFramework;
using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archer.Model.Tests;

public class AzureOpenAIChatClientFactoryTests
{
    [Fact]
    public void Create_throws_when_endpoint_is_not_configured()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions { Endpoint = null }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance,
            credentialFactory: () => new FakeCredential());

        Action act = () => factory.Create("gpt-x");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Endpoint*");
    }

    [Fact]
    public void Create_v1_surface_with_api_key_returns_a_chat_client()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "test-key",
                UseV1Surface = true,
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance);

        var client = factory.Create("gpt-x");
        client.Should().NotBeNull();
        client.GetService(typeof(Microsoft.Extensions.AI.IChatClient)).Should().NotBeNull();
    }

    [Fact]
    public void Create_v1_surface_with_credential_does_not_throw()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = null,
                UseV1Surface = true,
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance,
            credentialFactory: () => new FakeCredential());

        Action act = () => factory.Create("gpt-x");
        act.Should().NotThrow();
    }

    [Fact]
    public void Create_legacy_with_api_key_returns_a_chat_client()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "test-key",
                UseV1Surface = false,
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance);
        factory.Create("gpt-x").Should().NotBeNull();
    }

    [Fact]
    public void Create_legacy_with_credential_does_not_throw()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = null,
                UseV1Surface = false,
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance,
            credentialFactory: () => new FakeCredential());
        Action act = () => factory.Create("gpt-x");
        act.Should().NotThrow();
    }

    [Fact]
    public void Create_legacy_with_known_api_version_maps_to_service_version()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "k",
                UseV1Surface = false,
                ApiVersion = "2025-04-01-preview",
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance);
        Action act = () => factory.Create("gpt-x");
        act.Should().NotThrow();
    }

    [Fact]
    public void Create_legacy_with_unknown_api_version_falls_back_to_query_param()
    {
        var factory = new AzureOpenAIChatClientFactory(
            Options.Create(new AzureOpenAIOptions
            {
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "k",
                UseV1Surface = false,
                ApiVersion = "9999-zz-99",
            }),
            NullLogger<AzureOpenAIChatClientFactory>.Instance);
        Action act = () => factory.Create("gpt-x");
        act.Should().NotThrow();
    }

    /// <summary>
    /// Returns a deterministic fake token. Real DefaultAzureCredential would attempt actual
    /// Entra ID auth which requires environment setup we don't want in tests.
    /// </summary>
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
