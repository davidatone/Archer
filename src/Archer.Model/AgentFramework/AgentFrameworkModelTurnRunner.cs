using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archer.Application.Model;
using Archer.Application.Telemetry;
using Archer.Domain.Agents;
using Archer.Domain.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace Archer.Model.AgentFramework;

/// <summary>
/// IModelTurnRunner backed by Microsoft.Extensions.AI's IChatClient (the same primitive that
/// Microsoft.Agents.AI's ChatClientAgent wraps internally) plus an explicit tool-call loop so the
/// caller — the TurnWorkerGrain — keeps fence checkpoints around every tool invocation.
/// </summary>
public sealed class AgentFrameworkModelTurnRunner : IModelTurnRunner
{
    private readonly IChatClientFactory _factory;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AgentFrameworkModelTurnRunner> _logger;

    public AgentFrameworkModelTurnRunner(
        IChatClientFactory factory,
        IOptions<AzureOpenAIOptions> options,
        ILogger<AgentFrameworkModelTurnRunner> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public IAsyncEnumerable<IModelTurnUpdate> RunAsync(
        ModelTurnInput input,
        IReadOnlyList<ModelToolResultEntry> priorToolResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RunIteratorAsync(input, priorToolResults, cancellationToken);
    }

    private async IAsyncEnumerable<IModelTurnUpdate> RunIteratorAsync(
        ModelTurnInput input,
        IReadOnlyList<ModelToolResultEntry> priorToolResults,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, clientError) = TryCreateClient(input.ModelDeployment);
        if (client is null)
        {
            yield return new ModelErrorUpdate(clientError ?? "Failed to create chat client.");
            yield break;
        }

        yield return new ModelStartedUpdate(input.ModelDeployment);

        var messages = BuildMessages(input, priorToolResults);
        var chatOptions = BuildChatOptions(input);

        var (response, callError, finish, cancelled) = await CallModelAsync(client, messages, chatOptions, input, cancellationToken);
        if (cancelled) yield break;
        if (response is null)
        {
            yield return new ModelErrorUpdate(callError ?? "Model call failed.");
            yield break;
        }

        var assistantText = new System.Text.StringBuilder();
        var anyToolCall = false;
        foreach (var update in EmitResponseUpdates(response, assistantText, x => anyToolCall = anyToolCall || x))
        {
            yield return update;
        }

        if (!anyToolCall)
        {
            yield return BuildFinalAnswerUpdate(assistantText, response, finish);
        }
    }

    private (IChatClient? Client, string? Error) TryCreateClient(string deployment)
    {
        try { return (_factory.Create(deployment), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private ChatOptions BuildChatOptions(ModelTurnInput input)
    {
        var effort = input.ReasoningEffort ?? _options.ReasoningEffort;
        var summary = input.ReasoningSummary ?? _options.ReasoningSummary;
        return new ChatOptions
        {
            Tools = [.. input.Tools.Select(BuildTool)],
            ModelId = input.ModelDeployment,
            ToolMode = ChatToolMode.Auto,
            MaxOutputTokens = input.MaxCompletionTokens ?? _options.MaxCompletionTokens,
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = MapEffort(effort),
                    ReasoningSummaryVerbosity = MapSummary(summary),
                },
            },
        };
    }

    private async Task<(ChatResponse? Response, string? Error, string Finish, bool Cancelled)> CallModelAsync(
        IChatClient client, List<ChatMessage> messages, ChatOptions chatOptions, ModelTurnInput input, CancellationToken ct)
    {
        using var modelSpan = ArcherTelemetry.ActivitySource.StartActivity(
            "archer.model.call", ActivityKind.Client);
        modelSpan?.SetTag(ArcherTelemetry.Tags.Deployment, input.ModelDeployment);
        modelSpan?.SetTag(ArcherTelemetry.Tags.AgentId, input.AgentId);
        modelSpan?.SetTag(ArcherTelemetry.Tags.TurnId, input.TurnId.ToString());
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
            var finish = response?.FinishReason?.ToString() ?? "(none)";
            _logger.LogInformation("Model response finish reason: {Finish}", finish);
            modelSpan?.SetTag(ArcherTelemetry.Tags.FinishReason, finish);
            return (response, null, finish, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, null, "(cancelled)", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model call failed");
            modelSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ArcherTelemetry.ModelErrors.Add(1,
                new KeyValuePair<string, object?>(ArcherTelemetry.Tags.Deployment, input.ModelDeployment));
            return (null, ex.Message, "(error)", false);
        }
        finally
        {
            sw.Stop();
            ArcherTelemetry.ModelCallDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>(ArcherTelemetry.Tags.Deployment, input.ModelDeployment));
        }
    }

    private static IEnumerable<IModelTurnUpdate> EmitResponseUpdates(
        ChatResponse response, System.Text.StringBuilder assistantText, Action<bool> markToolCall)
    {
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fc:
                        markToolCall(true);
                        yield return new ModelToolCallUpdate(new ModelToolCall(fc.CallId, fc.Name, ConvertArgs(fc.Arguments)));
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        yield return new ModelReasoningUpdate(reasoning.Text);
                        break;
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        assistantText.Append(tc.Text);
                        yield return new ModelTextDeltaUpdate(tc.Text);
                        break;
                }
            }
        }
    }

    private static ModelFinalAnswerUpdate BuildFinalAnswerUpdate(
        System.Text.StringBuilder assistantText, ChatResponse response, string finish)
    {
        if (assistantText.Length > 0)
        {
            return new ModelFinalAnswerUpdate(assistantText.ToString());
        }
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            return new ModelFinalAnswerUpdate(response.Text!);
        }
        // No text means the model emitted reasoning but stopped before producing a visible
        // answer. Common cause: token budget exhausted mid-reasoning (`finish_reason: length`).
        var hint = string.Equals(finish, "Length", StringComparison.OrdinalIgnoreCase)
            ? "the response token budget was exhausted before the model emitted final text. Try a higher `MaxCompletionTokens` or break the prompt into smaller turns."
            : $"the model stopped without emitting text (finish reason: `{finish}`).";
        return new ModelFinalAnswerUpdate($"_(no final answer — {hint})_");
    }

    private static List<ChatMessage> BuildMessages(
        ModelTurnInput input,
        IReadOnlyList<ModelToolResultEntry> priorToolResults)
    {
        var list = new List<ChatMessage>
        {
            new(ChatRole.System, input.SystemInstructions),
        };

        foreach (var msg in input.Messages)
        {
            list.Add(new ChatMessage(MapRole(msg.Role), msg.Content));
        }

        // The Responses API requires every function-call output to be paired with the original
        // function-call message that produced it. Emit assistant + tool message pairs.
        foreach (var entry in priorToolResults)
        {
            list.Add(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(entry.ToolCallId, entry.ToolName, ToArgsDictionary(entry.Arguments))]));
            list.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(entry.ToolCallId, entry.ResultJson)]));
        }

        return list;
    }

    private static ResponseReasoningEffortLevel MapEffort(Archer.Domain.Agents.ReasoningEffort e) => e switch
    {
        Archer.Domain.Agents.ReasoningEffort.None => ResponseReasoningEffortLevel.None,
        Archer.Domain.Agents.ReasoningEffort.Minimal => ResponseReasoningEffortLevel.Minimal,
        Archer.Domain.Agents.ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
        Archer.Domain.Agents.ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
        Archer.Domain.Agents.ReasoningEffort.High => ResponseReasoningEffortLevel.High,
        _ => ResponseReasoningEffortLevel.Medium,
    };

    private static ResponseReasoningSummaryVerbosity MapSummary(ReasoningSummary s) => s switch
    {
        ReasoningSummary.Concise => ResponseReasoningSummaryVerbosity.Concise,
        ReasoningSummary.Detailed => ResponseReasoningSummaryVerbosity.Detailed,
        _ => ResponseReasoningSummaryVerbosity.Auto,
    };

    private static ChatRole MapRole(Archer.Domain.Agents.MessageRole role) => role switch
    {
        Archer.Domain.Agents.MessageRole.User => ChatRole.User,
        Archer.Domain.Agents.MessageRole.Assistant => ChatRole.Assistant,
        Archer.Domain.Agents.MessageRole.System => ChatRole.System,
        Archer.Domain.Agents.MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static AITool BuildTool(Domain.Tools.ToolDefinition def)
    {
        var schema = JsonSerializer.SerializeToElement(def.Parameters);
        return new ArcherFunctionDeclaration(def.Name, def.Description, schema);
    }

    private static Dictionary<string, object?> ToArgsDictionary(JsonObject? args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is null)
        {
            return dict;
        }
        foreach (var kvp in args)
        {
            dict[kvp.Key] = kvp.Value;
        }
        return dict;
    }

    private static JsonObject ConvertArgs(IDictionary<string, object?>? args)
    {
        var obj = new JsonObject();
        if (args is null)
        {
            return obj;
        }

        foreach (var kvp in args)
        {
            obj[kvp.Key] = kvp.Value switch
            {
                null => null,
                JsonNode n => n.DeepClone(),
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonNode.Parse(JsonSerializer.Serialize(kvp.Value)),
            };
        }
        return obj;
    }
}

/// <summary>
/// AIFunctionDeclaration that carries the JSON schema for an Azure OpenAI tool. We don't auto-execute
/// — calls bubble up as FunctionCallContent so the TurnWorkerGrain fences and routes them itself.
/// </summary>
internal sealed class ArcherFunctionDeclaration : AIFunctionDeclaration
{
    public ArcherFunctionDeclaration(string name, string description, JsonElement schema)
    {
        Name = name;
        Description = description;
        JsonSchema = schema;
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }
}
