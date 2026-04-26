using System.ClientModel;
using System.ClientModel.Primitives;
using Archer.Application.Model;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace Archer.Model.AgentFramework;

/// <summary>
/// Creates IChatClient instances backed by Azure OpenAI's Responses API. Implements
/// <see cref="IChatClientFactory"/> so the runner can be unit-tested against a fake.
/// Supports two endpoint shapes:
///   1. The new "v1 surface": <c>https://{resource}/openai/v1/responses</c>, model name in body, no api-version query.
///   2. The classic Azure deployments shape: <c>/openai/deployments/{deployment}/responses?api-version=...</c>.
/// </summary>
public sealed class AzureOpenAIChatClientFactory : IChatClientFactory
{
    private readonly AzureOpenAIOptions _options;
    private readonly Func<TokenCredential> _credentialFactory;
    private readonly ILogger<AzureOpenAIChatClientFactory> _logger;

    public AzureOpenAIChatClientFactory(
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIChatClientFactory> logger,
        Func<TokenCredential>? credentialFactory = null)
    {
        _options = options.Value;
        _logger = logger;
        _credentialFactory = credentialFactory ?? (() => new DefaultAzureCredential());
    }

    public IChatClient Create(string deployment)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException(
                "AzureOpenAI:Endpoint is not configured. Set AZURE_OPENAI_ENDPOINT or AzureOpenAI__Endpoint.");
        }

        return _options.UseV1Surface
            ? CreateV1SurfaceClient(deployment)
            : CreateLegacyDeploymentClient(deployment);
    }

    private const char UriSegmentSeparator = '/';

    /// <summary>Relative path on the Azure endpoint that exposes the v1 (Responses-API-compatible) surface.</summary>
    private const string AzureV1Path = "openai/v1/";

    private IChatClient CreateV1SurfaceClient(string deployment)
    {
        var endpointBase = _options.Endpoint!.TrimEnd(UriSegmentSeparator) + UriSegmentSeparator;
        var v1Base = new Uri(new Uri(endpointBase), AzureV1Path);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = v1Base,
        };

        ApiKeyCredential credential;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            credential = new ApiKeyCredential(_options.ApiKey);
            clientOptions.AddPolicy(new AzureApiKeyHeaderPolicy(_options.ApiKey), PipelinePosition.PerCall);
        }
        else
        {
            // Bearer-token via Entra ID
            var token = _credentialFactory().GetToken(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                CancellationToken.None);
            credential = new ApiKeyCredential(token.Token);
        }

        var client = new OpenAIClient(credential, clientOptions);
        var responsesClient = client.GetResponsesClient();
        _logger.LogDebug(
            "Built Azure OpenAI Responses client (v1 surface) at {BaseUri} for deployment {Deployment}",
            v1Base,
            deployment);
        return responsesClient.AsIChatClient(deployment);
    }

    private IChatClient CreateLegacyDeploymentClient(string deployment)
    {
        var clientOptions = BuildLegacyClientOptions(_options.ApiVersion);
        var endpointUri = new Uri(_options.Endpoint!);

        AzureOpenAIClient client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(endpointUri, _credentialFactory(), clientOptions)
            : new AzureOpenAIClient(endpointUri, new AzureKeyCredential(_options.ApiKey), clientOptions);

        ResponsesClient responsesClient = client.GetResponsesClient();
        _logger.LogDebug(
            "Built Azure OpenAI Responses client (legacy deployments) for {Deployment} (api-version={ApiVersion})",
            deployment,
            _options.ApiVersion ?? "(default)");
        return responsesClient.AsIChatClient(deployment);
    }

    private static AzureOpenAIClientOptions BuildLegacyClientOptions(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return new AzureOpenAIClientOptions();
        }

        if (TryMapServiceVersion(apiVersion, out var version))
        {
            return new AzureOpenAIClientOptions(version);
        }

        var options = new AzureOpenAIClientOptions();
        options.DefaultQueryParameters["api-version"] = apiVersion;
        return options;
    }

    private static bool TryMapServiceVersion(string apiVersion, out AzureOpenAIClientOptions.ServiceVersion version)
    {
        var token = "V" + apiVersion.Replace('-', '_').Replace("preview", "Preview", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse(token, ignoreCase: true, out version);
    }

}

/// <summary>Adds the Azure-style <c>api-key</c> header to every outgoing request.</summary>
internal sealed class AzureApiKeyHeaderPolicy : PipelinePolicy
{
    private readonly string _apiKey;

    public AzureApiKeyHeaderPolicy(string apiKey) => _apiKey = apiKey;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("api-key", _apiKey);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("api-key", _apiKey);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}
