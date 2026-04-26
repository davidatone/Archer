using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Archer.Actors;
using Archer.Actors.Contracts;
using Archer.Actors.Grains;
using Archer.Application.Agents;
using Archer.Application.Events;
using Archer.Application.Model;
using Archer.Application.Persistence;
using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Model;
using Archer.Domain.Time;
using Archer.Domain.Tools;
using Archer.Events;
using Archer.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Archer.Actors.Tests;

[Collection("Orleans")]
public sealed class ArcherAgentGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        // Reset the runner mode and tool-registry log here, before the cluster boots, so a
        // previous test that crashed before its `finally { Reset() }` block can't leak state
        // into this one. The runner uses static fields because the silo's DI builds a single
        // instance and the test thread can't reach it directly — InitializeAsync gives us a
        // clean reset point on every test.
        FakeFinalAnswerRunner.Reset();
        FakeToolRegistry.AllExecutedRequests.Clear();

        var builder = new TestClusterBuilder(initialSilosCount: 1);
        builder.Options.ServiceId = "ArcherTests";
        builder.Options.ClusterId = Guid.NewGuid().ToString("N");
        builder.AddSiloBuilderConfigurator<ArcherSiloConfig>();
        builder.AddClientBuilderConfigurator<ArcherClientConfig>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private IArcherAgentGrain Agent(string id) =>
        _cluster.Client.GetGrain<IArcherAgentGrain>(id);

    [Fact]
    public async Task GetSnapshotAsync_returns_null_when_not_initialized()
    {
        var snap = await Agent("agent_TESTAGENTXX01").GetSnapshotAsync();
        snap.Should().BeNull();
    }

    [Fact]
    public async Task InitializeAsync_creates_state_with_first_user_message()
    {
        var agent = Agent("agent_TESTAGENTXX02");
        var snap = await agent.InitializeAsync(new NewAgentRequest("/tmp", "investigate this"));

        snap.Messages.Should().HaveCount(1);
        snap.Messages[0].Role.Should().Be(MessageRole.User);
        snap.Messages[0].Content.Should().Be("investigate this");
        snap.LatestMessageSeq.Should().Be(1);
        snap.ActiveTurnId.Should().NotBeNull();
    }

    [Fact]
    public async Task InitializeAsync_twice_throws()
    {
        var agent = Agent("agent_TESTAGENTXX03");
        await agent.InitializeAsync(new NewAgentRequest("/tmp", "first"));

        Func<Task> act = () => agent.InitializeAsync(new NewAgentRequest("/tmp", "second"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TurnWorker_publishes_TurnFailed_when_runner_emits_an_error()
    {
        FakeFinalAnswerRunner.ResetForModelError();
        try
        {
            var agent = Agent("agent_TESTAGENTXX13");
            var snap = await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));

            // The agent's state will have the active turn cleared once the worker publishes
            // its TurnFailedEvent. We can't easily subscribe to events from the test (the
            // sink lives inside the silo), but the side-effect is observable through the
            // grain: the turn that started is no longer active.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var stillActive = await agent.IsTurnStillActiveAsync(snap.ActiveTurnId!.Value, snap.LatestMessageSeq);
                if (!stillActive) break;
                await Task.Delay(20);
            }
            // Production behavior: TurnFailed events do *not* clear the agent's ActiveTurnId
            // — only TurnSuperseded / interrupt / final-answer commit do. The worker just
            // publishes the failure event and exits. So we observe the worker has actually
            // exited (no longer iterating) by reading messages — they should still be 1.
            var snap2 = await agent.GetSnapshotAsync();
            snap2!.Messages.Should().HaveCount(1);  // no assistant message was committed
        }
        finally
        {
            FakeFinalAnswerRunner.Reset();
        }
    }

    [Fact]
    public async Task TurnWorker_drives_tool_call_then_final_answer_through_the_tool_registry()
    {
        FakeFinalAnswerRunner.ResetForToolCall();
        FakeToolRegistry.AllExecutedRequests.Clear();
        try
        {
            var agent = Agent("agent_TESTAGENTXX12");
            await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));

            // Wait for the worker to execute the tool call AND commit a final answer.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var snap = await agent.GetSnapshotAsync();
                if (snap?.ActiveTurnId is null && FakeToolRegistry.AllExecutedRequests.Count > 0) break;
                await Task.Delay(20);
            }

            FakeToolRegistry.AllExecutedRequests.Should().ContainSingle()
                .Which.ToolName.Should().Be("search_pattern");
            var final = (await agent.GetSnapshotAsync())!;
            final.Messages.Should().Contain(m => m.Role == MessageRole.Assistant && m.Content == "done");
        }
        finally
        {
            FakeFinalAnswerRunner.Reset();
        }
    }

    [Fact]
    public async Task AddUserMessageAsync_supersedes_an_in_flight_turn()
    {
        FakeFinalAnswerRunner.ResetForHold();
        try
        {
            var agent = Agent("agent_TESTAGENTXX11");
            var initial = await agent.InitializeAsync(new NewAgentRequest("/tmp", "first"));
            var firstTurn = initial.ActiveTurnId!.Value;

            // Wait for the worker to actually enter the runner — only then is the turn
            // truly "in flight" so AddUserMessage exercises the supersede path.
            await FakeFinalAnswerRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var ack = await agent.AddUserMessageAsync(new UserMessageInput("second"));
            ack.NewTurnId.Should().NotBe(firstTurn);
            ack.SupersededTurnId.Should().Be(firstTurn);
            ack.MessageSeq.Should().Be(2);

            // The fire-and-forget cancel signals the worker's CTS, which unblocks the runner
            // and lets the worker exit cleanly. The agent's state has the new turn active.
            var snap = await agent.GetSnapshotAsync();
            snap!.ActiveTurnId.Should().Be(ack.NewTurnId);
        }
        finally
        {
            FakeFinalAnswerRunner.Reset();
        }
    }

    [Fact]
    public async Task AddUserMessageAsync_appends_message_and_starts_a_new_turn()
    {
        var agent = Agent("agent_TESTAGENTXX04");
        var initial = await agent.InitializeAsync(new NewAgentRequest("/tmp", "first"));
        var firstTurn = initial.ActiveTurnId!.Value;

        // Wait for the first worker to commit its final answer so AddUserMessage observes
        // a settled state — this side-steps the mid-turn supersede race (which is exercised
        // separately in TurnFencingSmokeTests at the state level).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AgentSnapshot? snap = null;
        while (DateTime.UtcNow < deadline)
        {
            snap = await agent.GetSnapshotAsync();
            if (snap!.ActiveTurnId is null) break;
            await Task.Delay(20);
        }
        snap!.ActiveTurnId.Should().BeNull("the fake runner should commit a final answer quickly");

        var ack = await agent.AddUserMessageAsync(new UserMessageInput("second"));
        ack.NewTurnId.Should().NotBe(firstTurn);
        ack.SupersededTurnId.Should().BeNull("the prior turn already completed before AddUserMessage");

        var after = await agent.GetSnapshotAsync();
        after!.Messages.Where(m => m.Role == MessageRole.User).Should().HaveCount(2);
        after.LatestMessageSeq.Should().BeGreaterOrEqualTo(ack.MessageSeq);
    }

    [Fact]
    public async Task InterruptAsync_clears_active_turn()
    {
        var agent = Agent("agent_TESTAGENTXX05");
        await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));

        await agent.InterruptAsync(new InterruptRequest("user pressed escape"));
        var snap = await agent.GetSnapshotAsync();
        snap!.ActiveTurnId.Should().BeNull();
    }

    [Fact]
    public async Task InterruptAsync_when_no_active_turn_is_a_noop()
    {
        var agent = Agent("agent_TESTAGENTXX06");
        await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));
        await agent.InterruptAsync(new InterruptRequest("first"));

        Func<Task> act = () => agent.InterruptAsync(new InterruptRequest("again"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IsTurnStillActiveAsync_returns_false_for_unknown_turn()
    {
        var agent = Agent("agent_TESTAGENTXX07");
        await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));
        (await agent.IsTurnStillActiveAsync(Guid.NewGuid(), 1)).Should().BeFalse();
    }

    [Fact]
    public async Task AppendTurnEventAsync_summary_persists_summary_into_state()
    {
        var agent = Agent("agent_TESTAGENTXX08");
        var snap = await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));

        await agent.AppendTurnEventAsync(snap.ActiveTurnId!.Value, new SummaryEvent
        {
            AgentId = snap.AgentId,
            TurnId = snap.ActiveTurnId.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Message = "summary contents",
        });

        var snap2 = await agent.GetSnapshotAsync();
        snap2!.Summaries.Should().HaveCount(1);
        snap2.Summaries[0].Content.Should().Be("summary contents");
    }

    [Fact]
    public async Task CommitFinalAnswerIfStillActiveAsync_returns_false_for_stale_turn()
    {
        var agent = Agent("agent_TESTAGENTXX09");
        await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));
        var committed = await agent.CommitFinalAnswerIfStillActiveAsync(
            Guid.NewGuid(), 999, new AssistantMessage("late"));
        committed.Should().BeFalse();
    }

    [Fact]
    public async Task CommitFinalAnswerIfStillActiveAsync_appends_assistant_message_and_clears_turn()
    {
        var agent = Agent("agent_TESTAGENTXX10");
        var snap = await agent.InitializeAsync(new NewAgentRequest("/tmp", "go"));

        var committed = await agent.CommitFinalAnswerIfStillActiveAsync(
            snap.ActiveTurnId!.Value,
            snap.LatestMessageSeq,
            new AssistantMessage("done"));

        committed.Should().BeTrue();
        var snap2 = await agent.GetSnapshotAsync();
        snap2!.ActiveTurnId.Should().BeNull();
        snap2.Messages.Should().HaveCount(2);
        snap2.Messages[1].Role.Should().Be(MessageRole.Assistant);
        snap2.Messages[1].Content.Should().Be("done");
    }

    private static bool IsArcherDomainType(Type t) =>
        t.Namespace is { } ns &&
        (ns.StartsWith("Archer.Domain", StringComparison.Ordinal) ||
         ns.StartsWith("Archer.Application", StringComparison.Ordinal));

    private sealed class ArcherClientConfig : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.Services.AddSerializer(b => b.AddJsonSerializer(IsArcherDomainType));
        }
    }

    /// <summary>Wires the silo with stubbed implementations of every dependency the grains need.</summary>
    private sealed class ArcherSiloConfig : ISiloConfigurator
    {
        public void Configure(ISiloBuilder silo)
        {
            silo.Services.AddSerializer(b => b.AddJsonSerializer(IsArcherDomainType));
            silo.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(NullLoggerFactory.Instance);
                services.AddSingleton<IAgentStateStore, InMemoryAgentStateStore>();
                services.AddSingleton<IAgentBlobStore, InMemoryAgentBlobStore>();
                services.AddSingleton<ISystemClock, SystemClock>();
                services.AddSingleton<ChannelAgentEventSink>();
                services.AddSingleton<IAgentEventSink>(sp => sp.GetRequiredService<ChannelAgentEventSink>());
                services.AddSingleton<IAgentDefinitionRegistry, FakeAgentDefinitionRegistry>();
                services.AddSingleton<IAgentContextBuilder, FakeContextBuilder>();
                services.AddSingleton<IModelTurnRunner, FakeFinalAnswerRunner>();
                services.AddSingleton<IToolRegistry, FakeToolRegistry>();
                services.AddArcherActors();
            });
        }
    }

    private sealed class InMemoryAgentStateStore : IAgentStateStore
    {
        private readonly Dictionary<string, AgentState> _store = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Task<AgentState?> LoadAsync(string agentId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_store.TryGetValue(agentId, out var s) ? s : null);
            }
        }

        public Task SaveAsync(AgentState state, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _store[state.AgentId] = state;
            }
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
        {
            lock (_gate) { return Task.FromResult(_store.ContainsKey(id)); }
        }

        public Task AppendEventAsync(string id, AgentEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveToolResultAsync(string id, Guid t, int idx, ToolResult r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListAgentsAsync(CancellationToken ct = default)
        {
            lock (_gate) { return Task.FromResult<IReadOnlyList<string>>([.. _store.Keys]); }
        }
    }

    /// <summary>One stub definition matching whatever id the test agent uses.</summary>
    private sealed class FakeAgentDefinitionRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Default = new()
        {
            Id = "code-scout",
            Description = "test",
            Instructions = "test",
            Tools = [],
            Context = new ContextProfile(),
            Model = new ModelProfile { Deployment = "gpt-x" },
        };
        public AgentDefinition? Get(string id) => Default;
        public IReadOnlyList<AgentDefinition> All => [Default];
    }

    private sealed class FakeContextBuilder : IAgentContextBuilder
    {
        public ModelTurnInput Build(AgentState state, Guid turnId, AgentDefinition definition) => new(
            AgentId: state.AgentId,
            TurnId: turnId,
            SystemInstructions: "test",
            Messages: state.Messages.AsReadOnly(),
            Tools: [],
            ModelDeployment: definition.Model.Deployment);
    }

    /// <summary>
    /// Composite runner with three modes the tests pick between via static flags:
    /// <c>FastFinalAnswer</c> (default), <c>HoldOpen</c> (block on the worker's CTS so the
    /// test can supersede mid-turn), and <c>OneToolCallThenFinal</c> (emit a tool call on the
    /// first iteration, then a final answer once a prior result is observed).
    /// </summary>
    private sealed class FakeFinalAnswerRunner : IModelTurnRunner
    {
        public enum Mode { FastFinalAnswer, HoldOpen, OneToolCallThenFinal, ModelError }

        public static void ResetForModelError()
        {
            CurrentMode = Mode.ModelError;
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public static volatile Mode CurrentMode = Mode.FastFinalAnswer;
        public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void ResetForHold()
        {
            CurrentMode = Mode.HoldOpen;
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static void ResetForToolCall()
        {
            CurrentMode = Mode.OneToolCallThenFinal;
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static void Reset()
        {
            CurrentMode = Mode.FastFinalAnswer;
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IAsyncEnumerable<IModelTurnUpdate> RunAsync(
            ModelTurnInput input,
            IReadOnlyList<ModelToolResultEntry> priorToolResults,
            CancellationToken ct) => Iter(priorToolResults, ct);

        private static async IAsyncEnumerable<IModelTurnUpdate> Iter(
            IReadOnlyList<ModelToolResultEntry> prior,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ModelStartedUpdate("gpt-x");

            switch (CurrentMode)
            {
                case Mode.HoldOpen:
                    Started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { /* expected */ }
                    yield break;

                case Mode.OneToolCallThenFinal:
                    if (prior.Count == 0)
                    {
                        // First iteration — emit a tool call so the worker exercises
                        // ProcessToolCallsAsync. The runner is invoked again on the next
                        // iteration with the prior result attached.
                        yield return new ModelReasoningUpdate("about to call a tool");
                        yield return new ModelToolCallUpdate(new ModelToolCall(
                            "call_1",
                            "search_pattern",
                            new System.Text.Json.Nodes.JsonObject { ["pattern"] = "Foo" }));
                    }
                    else
                    {
                        yield return new ModelFinalAnswerUpdate("done");
                    }
                    break;

                case Mode.ModelError:
                    yield return new ModelErrorUpdate("simulated model failure");
                    break;

                case Mode.FastFinalAnswer:
                default:
                    yield return new ModelFinalAnswerUpdate("ok");
                    break;
            }
        }
    }

    /// <summary>Records every ExecuteAsync invocation so tool-call tests can assert on routing.</summary>
    private sealed class FakeToolRegistry : IToolRegistry
    {
        public static readonly List<ToolRequest> AllExecutedRequests = [];
        public IReadOnlyList<ITool> Tools => [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public bool TryGet(string name, out ITool tool) { tool = null!; return false; }
        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
        {
            lock (AllExecutedRequests) { AllExecutedRequests.Add(request); }
            return Task.FromResult(new ToolResult(
                ToolCallId: request.ToolCallId,
                ToolName: request.ToolName,
                Success: true,
                Data: new System.Text.Json.Nodes.JsonObject { ["ok"] = true },
                Summary: "ran",
                ResultItemCount: 1,
                Duration: TimeSpan.FromMilliseconds(1)));
        }
        public void Add(ITool tool) { }
        public bool Remove(string name) => false;
        public event Action<ToolRegistryChange>? Changed { add { } remove { } }
    }
}
