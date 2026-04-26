using System.Text.Json;
using Archer.Actors.Contracts;
using Archer.Application.Agents;
using Archer.Application.Events;
using Archer.Application.Model;
using Archer.Application.Persistence;
using Archer.Application.Telemetry;
using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Model;
using Archer.Domain.Time;
using Archer.Domain.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Archer.Actors.Grains;

// Each turn gets a fresh grain (keyed by Guid TurnId), so re-entrancy buys nothing —
// keeping the default single-threaded execution model avoids _cts mutation races.
public sealed class TurnWorkerGrain : Grain, ITurnWorkerGrain
{
    private readonly IAgentStateStore _store;
    private readonly IAgentEventSink _events;
    private readonly IAgentContextBuilder _contextBuilder;
    private readonly IModelTurnRunner _modelRunner;
    private readonly IToolRegistry _tools;
    private readonly IAgentDefinitionRegistry _definitions;
    private readonly ISystemClock _clock;
    private readonly TurnWorkerOptions _options;
    private readonly ILogger<TurnWorkerGrain> _logger;

    private CancellationTokenSource? _cts;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint", "S107:Methods should not have too many parameters",
        Justification = "DI constructor wiring all turn-worker dependencies; bundling them into a record would obscure the resolution chain.")]
    public TurnWorkerGrain(
        IAgentStateStore store,
        IAgentEventSink events,
        IAgentContextBuilder contextBuilder,
        IModelTurnRunner modelRunner,
        IToolRegistry tools,
        IAgentDefinitionRegistry definitions,
        ISystemClock clock,
        IOptions<TurnWorkerOptions> options,
        ILogger<TurnWorkerGrain> logger)
    {
        _store = store;
        _events = events;
        _contextBuilder = contextBuilder;
        _modelRunner = modelRunner;
        _tools = tools;
        _definitions = definitions;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunTurnAsync(TurnRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ct = await ResetTurnCtsAsync().ConfigureAwait(false);
        var agent = GrainFactory.GetGrain<IArcherAgentGrain>(request.AgentId);

        using var turnSpan = StartTurnSpan(request);

        try
        {
            await RunTurnLoopAsync(request, agent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Turn {TurnId} cancelled.", request.TurnId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turn worker for {TurnId} crashed", request.TurnId);
            await PublishFailureAsync(request, ex.Message);
        }
    }

    public async Task CancelAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Dispose the final CTS when the grain unloads so its kernel handle is freed promptly.
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        await base.OnDeactivateAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CancellationToken> ResetTurnCtsAsync()
    {
        // Dispose any prior turn's CTS — Cancel alone leaks the underlying handle.
        var prior = _cts;
        _cts = new CancellationTokenSource();
        if (prior is not null)
        {
            await prior.CancelAsync().ConfigureAwait(false);
            prior.Dispose();
        }
        return _cts.Token;
    }

    private static System.Diagnostics.Activity? StartTurnSpan(TurnRunRequest request)
    {
        var turnSpan = ArcherTelemetry.ActivitySource.StartActivity(
            "archer.turn",
            System.Diagnostics.ActivityKind.Internal);
        turnSpan?.SetTag(ArcherTelemetry.Tags.AgentId, request.AgentId);
        turnSpan?.SetTag(ArcherTelemetry.Tags.TurnId, request.TurnId.ToString());
        ArcherTelemetry.TurnsStarted.Add(1,
            new KeyValuePair<string, object?>(ArcherTelemetry.Tags.AgentId, request.AgentId));
        return turnSpan;
    }

    private async Task RunTurnLoopAsync(TurnRunRequest request, IArcherAgentGrain agent, CancellationToken ct)
    {
        var priorResults = new List<ModelToolResultEntry>();
        var toolIndex = 0;
        for (var iter = 0; iter < _options.MaxIterations; iter++)
        {
            if (!await agent.IsTurnStillActiveAsync(request.TurnId, request.StartedAtMessageSeq))
            {
                return;
            }

            var (state, definition, failure) = await ResolveTurnContextAsync(request, ct);
            if (failure is not null)
            {
                await PublishFailureAsync(request, failure);
                return;
            }

            var input = _contextBuilder.Build(state!, request.TurnId, definition!);
            await _events.PublishAsync(new ModelStartedEvent
            {
                AgentId = request.AgentId,
                TurnId = request.TurnId,
                CreatedAtUtc = _clock.UtcNow,
                Model = input.ModelDeployment,
            }, ct);

            if (await RunOneIterationAsync(request, agent, state!, input, priorResults, () => ++toolIndex, ct))
            {
                return;
            }
        }
        await PublishFailureAsync(request, "Max tool iterations exceeded without a final answer.");
    }

    /// <summary>Returns true when the loop should exit (final answer committed, error, or supersession).</summary>
    private async Task<bool> RunOneIterationAsync(
        TurnRunRequest request, IArcherAgentGrain agent,
        Archer.Domain.Agents.AgentState state,
        Archer.Domain.Model.ModelTurnInput input,
        List<ModelToolResultEntry> priorResults,
        Func<int> nextIndex,
        CancellationToken ct)
    {
        var modelOutcome = await CollectModelUpdatesAsync(input, priorResults, request, ct);
        if (modelOutcome.ErrorText is not null)
        {
            await PublishFailureAsync(request, modelOutcome.ErrorText);
            return true;
        }
        if (!await agent.IsTurnStillActiveAsync(request.TurnId, request.StartedAtMessageSeq))
        {
            return true;
        }
        if (modelOutcome.ToolCalls.Count == 0)
        {
            await CommitFinalAnswerAsync(agent, request, modelOutcome.FinalText);
            return true;
        }
        return !await ProcessToolCallsAsync(modelOutcome.ToolCalls, agent, request, state, priorResults, nextIndex, ct);
    }

    private async Task<(Archer.Domain.Agents.AgentState? State, Archer.Domain.Agents.AgentDefinition? Definition, string? Failure)>
        ResolveTurnContextAsync(TurnRunRequest request, CancellationToken ct)
    {
        var state = await _store.LoadAsync(request.AgentId, ct);
        if (state is null)
        {
            return (null, null, "Agent state vanished mid-turn.");
        }
        var definition = _definitions.Get(state.AgentDefinitionId);
        if (definition is null)
        {
            return (state, null,
                $"Agent definition '{state.AgentDefinitionId}' is not registered. " +
                "Drop a YAML in the agents/ directory or pass --agent <id>.");
        }
        return (state, definition, null);
    }

    private async Task<ModelOutcome> CollectModelUpdatesAsync(
        Archer.Domain.Model.ModelTurnInput input,
        List<ModelToolResultEntry> priorResults,
        TurnRunRequest request,
        CancellationToken ct)
    {
        var toolCalls = new List<ModelToolCall>();
        string? finalText = null;
        string? errorText = null;
        await foreach (var update in _modelRunner.RunAsync(input, priorResults, ct))
        {
            switch (update)
            {
                case ModelToolCallUpdate tcu:
                    toolCalls.Add(tcu.ToolCall);
                    break;
                case ModelFinalAnswerUpdate fa:
                    finalText = fa.Text;
                    break;
                case ModelReasoningUpdate r:
                    await _events.PublishAsync(new ReasoningEvent
                    {
                        AgentId = request.AgentId,
                        TurnId = request.TurnId,
                        CreatedAtUtc = _clock.UtcNow,
                        Text = r.Text,
                    }, ct);
                    break;
                case ModelErrorUpdate err:
                    errorText = err.Error;
                    break;
            }
        }
        return new ModelOutcome(toolCalls, finalText, errorText);
    }

    private async Task CommitFinalAnswerAsync(IArcherAgentGrain agent, TurnRunRequest request, string? finalText)
    {
        var committed = await agent.CommitFinalAnswerIfStillActiveAsync(
            request.TurnId,
            request.StartedAtMessageSeq,
            new AssistantMessage(finalText ?? string.Empty));
        if (committed)
        {
            ArcherTelemetry.TurnsCompleted.Add(1,
                new KeyValuePair<string, object?>(ArcherTelemetry.Tags.AgentId, request.AgentId));
        }
        else
        {
            ArcherTelemetry.TurnsSuperseded.Add(1,
                new KeyValuePair<string, object?>(ArcherTelemetry.Tags.AgentId, request.AgentId));
            _logger.LogDebug("Final-answer commit rejected by agent (superseded).");
        }
    }

    /// <summary>Returns false when the caller should bail out (cancellation or supersession).</summary>
    private async Task<bool> ProcessToolCallsAsync(
        List<ModelToolCall> toolCalls, IArcherAgentGrain agent, TurnRunRequest request,
        Archer.Domain.Agents.AgentState state, List<ModelToolResultEntry> priorResults,
        Func<int> nextIndex, CancellationToken ct)
    {
        foreach (var call in toolCalls)
        {
            if (!await agent.IsTurnStillActiveAsync(request.TurnId, request.StartedAtMessageSeq))
            {
                return false;
            }

            await PublishToolStartedAsync(call, request, ct);

            var (result, cancelled) = await ExecuteToolAsync(call, state.RepoRoot, request, ct);
            if (cancelled) return false;

            if (!await agent.IsTurnStillActiveAsync(request.TurnId, request.StartedAtMessageSeq))
            {
                return false;
            }

            await agent.RecordToolResultAsync(request.TurnId, nextIndex(), result);
            await _events.PublishAsync(new ToolCallCompletedEvent
            {
                AgentId = request.AgentId,
                TurnId = request.TurnId,
                CreatedAtUtc = _clock.UtcNow,
                ToolCallId = call.ToolCallId,
                ToolName = call.ToolName,
                Duration = result.Duration,
                ResultItemCount = result.ResultItemCount,
                Summary = result.Summary,
                Success = result.Success,
                Error = result.Error,
            }, ct);

            priorResults.Add(new ModelToolResultEntry(
                call.ToolCallId, call.ToolName, call.Arguments,
                JsonSerializer.Serialize(result.Data)));
        }
        return true;
    }

    private async Task PublishToolStartedAsync(ModelToolCall call, TurnRunRequest request, CancellationToken ct)
    {
        await _events.PublishAsync(new ToolCallStartedEvent
        {
            AgentId = request.AgentId,
            TurnId = request.TurnId,
            CreatedAtUtc = _clock.UtcNow,
            ToolCallId = call.ToolCallId,
            ToolName = call.ToolName,
            Arguments = call.Arguments,
        }, ct);
    }

    private async Task<(ToolResult Result, bool Cancelled)> ExecuteToolAsync(
        ModelToolCall call, string repoRoot, TurnRunRequest request, CancellationToken ct)
    {
        var toolReq = new ToolRequest(
            ToolCallId: call.ToolCallId,
            ToolName: call.ToolName,
            Arguments: call.Arguments,
            RepoRoot: repoRoot,
            AgentId: request.AgentId);

        using var toolSpan = ArcherTelemetry.ActivitySource.StartActivity(
            $"archer.tool.{call.ToolName}",
            System.Diagnostics.ActivityKind.Internal);
        toolSpan?.SetTag(ArcherTelemetry.Tags.AgentId, request.AgentId);
        toolSpan?.SetTag(ArcherTelemetry.Tags.TurnId, request.TurnId.ToString());
        toolSpan?.SetTag(ArcherTelemetry.Tags.ToolName, call.ToolName);
        toolSpan?.SetTag(ArcherTelemetry.Tags.ToolCallId, call.ToolCallId);
        ArcherTelemetry.ToolCalls.Add(1,
            new KeyValuePair<string, object?>(ArcherTelemetry.Tags.ToolName, call.ToolName));

        try
        {
            var result = await _tools.ExecuteAsync(toolReq, ct);
            ArcherTelemetry.ToolDurationMs.Record(result.Duration.TotalMilliseconds,
                new KeyValuePair<string, object?>(ArcherTelemetry.Tags.ToolName, call.ToolName));
            toolSpan?.SetStatus(result.Success
                ? System.Diagnostics.ActivityStatusCode.Ok
                : System.Diagnostics.ActivityStatusCode.Error,
                result.Error);
            return (result, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (default!, true);
        }
    }

    private sealed record ModelOutcome(List<ModelToolCall> ToolCalls, string? FinalText, string? ErrorText);

    private async Task PublishFailureAsync(TurnRunRequest request, string error)
    {
        ArcherTelemetry.TurnsFailed.Add(1,
            new KeyValuePair<string, object?>(ArcherTelemetry.Tags.AgentId, request.AgentId));
        await _events.PublishAsync(new TurnFailedEvent
        {
            AgentId = request.AgentId,
            TurnId = request.TurnId,
            CreatedAtUtc = _clock.UtcNow,
            Error = error,
        });
    }
}

public sealed class TurnWorkerOptions
{
    public const string SectionName = "TurnWorker";

    /// <summary>Soft cap on tool-call iterations per turn; 9999 = effectively unlimited.</summary>
    public int MaxIterations { get; set; } = 9999;
}
