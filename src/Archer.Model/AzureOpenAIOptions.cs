using Archer.Domain.Agents;

namespace Archer.Model;

/// <summary>
/// Connection-level Azure OpenAI configuration. Per-agent settings (model deployment,
/// reasoning, max-tokens) live on each AgentDefinition; values here are fall-back defaults
/// used when a definition omits them or for ad-hoc/uninitialized agents.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. https://my-resource.openai.azure.com</summary>
    public string? Endpoint { get; set; }

    /// <summary>API key for Azure OpenAI. If null, AzureCliCredential / DefaultAzureCredential is used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Default deployment / model name when none is supplied per-agent.</summary>
    public string DefaultDeployment { get; set; } = "gpt-5-mini";

    /// <summary>Azure OpenAI API version (e.g. "2025-04-01-preview"). Null = SDK default.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// When true (default), use the new "v1 surface" routing: <c>/openai/v1/responses</c> with
    /// the model in the request body. When false, use the legacy deployments-style routing.
    /// </summary>
    public bool UseV1Surface { get; set; } = true;

    /// <summary>Fallback completion-token cap when the agent definition doesn't override.</summary>
    public int MaxCompletionTokens { get; set; } = 16384;

    /// <summary>Fallback reasoning effort when the agent definition doesn't override.</summary>
    public ReasoningEffort ReasoningEffort { get; set; } = ReasoningEffort.Medium;

    /// <summary>Fallback reasoning summary verbosity when the agent definition doesn't override.</summary>
    public ReasoningSummary ReasoningSummary { get; set; } = ReasoningSummary.Auto;
}
