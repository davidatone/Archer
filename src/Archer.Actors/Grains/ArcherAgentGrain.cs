using Archer.Actors.Contracts;
using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Time;
using Archer.Domain.Tools;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Archer.Actors.Grains;

public sealed class ArcherAgentGrain : Grain, IArcherAgentGrain
{
    private readonly IAgentStateStore _store;
    private readonly IAgentEventSink _events;
    private readonly ISystemClock _clock;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ArcherAgentGrain> _logger;

    private AgentState? _state;

    public ArcherAgentGrain(
        IAgentStateStore store,
        IAgentEventSink events,
        ISystemClock clock,
        IGrainFactory grainFactory,
        ILogger<ArcherAgentGrain> logger)
    {
        _store = store;
        _events = events;
        _clock = clock;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _state = await _store.LoadAsync(this.GetPrimaryKeyString(), cancellationToken);
    }

    public async Task<AgentSnapshot> InitializeAsync(NewAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agentId = this.GetPrimaryKeyString();
        var now = _clock.UtcNow;

        if (_state is not null)
        {
            throw new InvalidOperationException($"Agent {agentId} already initialized.");
        }

        var first = new AgentMessage
        {
            Seq = 1,
            Role = MessageRole.User,
            Content = request.FirstUserPrompt,
            CreatedAtUtc = now,
        };

        var turnId = Guid.NewGuid();
        _state = new AgentState
        {
            AgentId = agentId,
            RepoRoot = Path.GetFullPath(request.RepoRoot),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LatestMessageSeq = 1,
            ActiveTurnId = turnId,
            ActiveTurnStartedAtSeq = 1,
            AgentDefinitionId = request.AgentDefinitionId,
            ModelDeployment = request.ModelDeployment,
            MaxCompletionTokens = request.MaxCompletionTokens,
        };
        _state.Messages.Add(first);

        await SaveAndPublishTurnStartedAsync(turnId);
        ScheduleWorker(turnId, 1);
        return AgentSnapshot.From(_state);
    }

    public async Task<UserMessageAccepted> AddUserMessageAsync(UserMessageInput input)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(input);
        var now = _clock.UtcNow;
        var supersededTurn = _state!.ActiveTurnId;

        _state.LatestMessageSeq++;
        var seq = _state.LatestMessageSeq;
        _state.Messages.Add(new AgentMessage
        {
            Seq = seq,
            Role = MessageRole.User,
            Content = input.Content,
            CreatedAtUtc = now,
        });

        var newTurnId = Guid.NewGuid();
        _state.ActiveTurnId = newTurnId;
        _state.ActiveTurnStartedAtSeq = seq;
        _state.UpdatedAtUtc = now;

        if (supersededTurn is { } stale && stale != newTurnId)
        {
            await _events.PublishAsync(new TurnSupersededEvent
            {
                AgentId = _state.AgentId,
                TurnId = stale,
                CreatedAtUtc = now,
                Reason = "New user message received.",
            });
            // Fire-and-forget the cancel: the prior worker is calling back into us
            // (IsTurnStillActiveAsync), so awaiting its CancelAsync would deadlock under
            // Orleans' default non-reentrancy. The state-update above already supersedes
            // the prior turn — the worker will exit on its next fence check regardless.
            _ = TryCancelWorkerAsync(stale);
        }

        await SaveAndPublishTurnStartedAsync(newTurnId);
        ScheduleWorker(newTurnId, seq);

        return new UserMessageAccepted(seq, newTurnId, supersededTurn != newTurnId ? supersededTurn : null);
    }

    public async Task InterruptAsync(InterruptRequest request)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(request);
        if (_state!.ActiveTurnId is null)
        {
            return;
        }

        var stale = _state.ActiveTurnId.Value;
        _state.ActiveTurnId = null;
        _state.ActiveTurnStartedAtSeq = null;
        _state.UpdatedAtUtc = _clock.UtcNow;

        await _events.PublishAsync(new TurnSupersededEvent
        {
            AgentId = _state.AgentId,
            TurnId = stale,
            CreatedAtUtc = _clock.UtcNow,
            Reason = request.Reason,
        });

        // Fire-and-forget; see AddUserMessageAsync for the rationale.
        _ = TryCancelWorkerAsync(stale);
        await _store.SaveAsync(_state);
    }

    public Task<AgentSnapshot?> GetSnapshotAsync() =>
        Task.FromResult(_state is null ? null : AgentSnapshot.From(_state));

    public Task<bool> IsTurnStillActiveAsync(Guid turnId, long messageSeq)
    {
        EnsureLoaded();
        return Task.FromResult(_state!.IsTurnActive(turnId, messageSeq));
    }

    public async Task AppendTurnEventAsync(Guid turnId, AgentEvent evt)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(evt);
        if (evt is SummaryEvent summary)
        {
            _state!.Summaries.Add(new Summary
            {
                TurnId = turnId,
                Content = summary.Message,
                CreatedAtUtc = summary.CreatedAtUtc,
            });
            await _store.SaveAsync(_state);
        }
        await _events.PublishAsync(evt);
    }

    public async Task RecordToolResultAsync(Guid turnId, int index, ToolResult result)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(result);
        await _store.SaveToolResultAsync(_state!.AgentId, turnId, index, result);
    }

    public async Task<bool> CommitFinalAnswerIfStillActiveAsync(
        Guid turnId,
        long messageSeq,
        AssistantMessage final)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(final);

        if (!_state!.IsTurnActive(turnId, messageSeq))
        {
            return false;
        }

        var now = _clock.UtcNow;
        _state.LatestMessageSeq++;
        _state.Messages.Add(new AgentMessage
        {
            Seq = _state.LatestMessageSeq,
            Role = MessageRole.Assistant,
            Content = final.Text,
            CreatedAtUtc = now,
        });
        _state.ActiveTurnId = null;
        _state.ActiveTurnStartedAtSeq = null;
        _state.UpdatedAtUtc = now;

        await _store.SaveAsync(_state);
        await _events.PublishAsync(new FinalAnswerEvent
        {
            AgentId = _state.AgentId,
            TurnId = turnId,
            CreatedAtUtc = now,
            Text = final.Text,
        });
        await _events.PublishAsync(new TurnCompletedEvent
        {
            AgentId = _state.AgentId,
            TurnId = turnId,
            CreatedAtUtc = now,
        });
        return true;
    }

    private void EnsureLoaded()
    {
        if (_state is null)
        {
            throw new InvalidOperationException(
                $"Agent {this.GetPrimaryKeyString()} has not been initialized.");
        }
    }

    private async Task SaveAndPublishTurnStartedAsync(Guid turnId)
    {
        await _store.SaveAsync(_state!);
        await _events.PublishAsync(new TurnStartedEvent
        {
            AgentId = _state!.AgentId,
            TurnId = turnId,
            CreatedAtUtc = _clock.UtcNow,
            MessageSeq = _state.LatestMessageSeq,
        });
    }

    private void ScheduleWorker(Guid turnId, long messageSeq)
    {
        var worker = _grainFactory.GetGrain<ITurnWorkerGrain>(turnId);
        // Fire-and-forget: TurnWorkerGrain owns its own lifecycle and emits events back.
        _ = worker.RunTurnAsync(new TurnRunRequest(_state!.AgentId, turnId, messageSeq));
    }

    private async Task TryCancelWorkerAsync(Guid turnId)
    {
        try
        {
            var worker = _grainFactory.GetGrain<ITurnWorkerGrain>(turnId);
            await worker.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancel for turn {TurnId} did not complete cleanly", turnId);
        }
    }
}
