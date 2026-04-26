using Archer.Actors.Contracts;
using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Tools;
using Orleans;

namespace Archer.Tui.Sessions;

/// <summary>
/// Holds per-agent UI state and subscribes to its event stream. The event pump runs on a
/// background task; all state mutations and event invocations are marshaled through a
/// caller-supplied <c>dispatch</c> action so consumers (the Terminal.Gui main thread) see
/// a serialized view of changes. A single-flight guard prevents two refreshes from
/// interleaving on the in-memory collections.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private static readonly Action<Action> SynchronousDispatch = a => a();

    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IArcherAgentGrain> _grain;
    private readonly IAgentEventSink _sink;
    private readonly IAgentBlobStore? _blobStore;
    private readonly Action<Action> _dispatch;
    private readonly Task _pumpTask;
    private int _refreshInFlight;

    public string AgentId { get; }
    public string RepoRoot { get; }
    public string AgentType { get; }
    public List<AgentEvent> Events { get; } = [];
    public List<AgentMessage> Messages { get; } = [];
    public List<TodoItem> Todos { get; } = [];
    public Guid? ActiveTurnId { get; private set; }
    public bool IsThinking { get; private set; }

    public event Action<AgentEvent>? EventReceived;
    public event Action? StateChanged;

    public AgentSession(
        string agentId,
        string repoRoot,
        string agentType,
        IGrainFactory grainFactory,
        IAgentEventSink sink,
        IAgentBlobStore? blobStore = null,
        Action<Action>? dispatch = null)
        : this(agentId, repoRoot, agentType, () => grainFactory.GetGrain<IArcherAgentGrain>(agentId), sink, blobStore, dispatch)
    {
    }

    public AgentSession(
        string agentId,
        string repoRoot,
        string agentType,
        Func<IArcherAgentGrain> grainResolver,
        IAgentEventSink sink,
        IAgentBlobStore? blobStore = null,
        Action<Action>? dispatch = null)
    {
        AgentId = agentId;
        RepoRoot = repoRoot;
        AgentType = agentType;
        _grain = grainResolver;
        _sink = sink;
        _blobStore = blobStore;
        _dispatch = dispatch ?? SynchronousDispatch;
        _pumpTask = Task.Run(PumpAsync);
    }

    public IArcherAgentGrain Grain => _grain();

    public async Task RefreshSnapshotAsync()
    {
        // Single-flight: a tool-call refresh kicked off while the previous one is still
        // running would otherwise race the list mutations.
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }
        try
        {
            var snap = await Grain.GetSnapshotAsync();
            if (snap is null)
            {
                return;
            }

            // The task list lives in the per-agent blob store (owned by the task-tracker tool) —
            // fetch alongside the snapshot so the side pane stays in sync without a grain method.
            var todoList = _blobStore is null
                ? null
                : await _blobStore.LoadAsync<TodoListTool.TodoList>(AgentId, "todos");

            _dispatch(() =>
            {
                Messages.Clear();
                Messages.AddRange(snap.Messages);
                Todos.Clear();
                if (todoList is not null)
                {
                    Todos.AddRange(todoList.Items);
                }
                ActiveTurnId = snap.ActiveTurnId;
                StateChanged?.Invoke();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var evt in _sink.SubscribeAsync(AgentId, _cts.Token))
            {
                _dispatch(() =>
                {
                    Events.Add(evt);
                    switch (evt)
                    {
                        case TurnStartedEvent ts:
                            ActiveTurnId = ts.TurnId;
                            IsThinking = true;
                            break;
                        case ModelStartedEvent:
                            IsThinking = true;
                            break;
                        case TurnCompletedEvent or TurnFailedEvent or TurnSupersededEvent:
                            ActiveTurnId = null;
                            IsThinking = false;
                            break;
                    }
                    EventReceived?.Invoke(evt);
                });

                // Fetch snapshot off-thread; the dispatch inside RefreshSnapshotAsync re-marshals
                // the mutation back to the UI thread.
                if (evt is TurnCompletedEvent or TurnFailedEvent or TurnSupersededEvent
                       or ToolCallCompletedEvent or SummaryEvent)
                {
                    _ = RefreshSnapshotAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disposal — pump shuts down via _cts
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch { /* ignored */ }
        _cts.Dispose();
    }
}
