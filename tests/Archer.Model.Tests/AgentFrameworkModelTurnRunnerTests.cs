using System.Text.Json.Nodes;
using Archer.Application.Model;
using Archer.Domain.Agents;
using Archer.Domain.Model;
using Archer.Domain.Tools;
using Archer.Model;
using Archer.Model.AgentFramework;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archer.Model.Tests;

public class AgentFrameworkModelTurnRunnerTests
{
    private const string Deployment = "gpt-x";

    private static AgentFrameworkModelTurnRunner Runner(
        IChatClientFactory factory,
        AzureOpenAIOptions? options = null) =>
        new(factory,
            Options.Create(options ?? new AzureOpenAIOptions { Endpoint = "https://x" }),
            NullLogger<AgentFrameworkModelTurnRunner>.Instance);

    private static ModelTurnInput Input() => new(
        AgentId: "agent_TESTAGENTXXX1",
        TurnId: Guid.NewGuid(),
        SystemInstructions: "system",
        Messages:
        [
            new AgentMessage
            {
                Seq = 1,
                Role = MessageRole.User,
                Content = "hi",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
        ],
        Tools: [],
        ModelDeployment: Deployment);

    private static async Task<List<IModelTurnUpdate>> Drain(AgentFrameworkModelTurnRunner runner, ModelTurnInput input, IReadOnlyList<ModelToolResultEntry>? prior = null)
    {
        var collected = new List<IModelTurnUpdate>();
        await foreach (var u in runner.RunAsync(input, prior ?? [], CancellationToken.None))
        {
            collected.Add(u);
        }
        return collected;
    }

    [Fact]
    public async Task RunAsync_emits_error_when_factory_throws()
    {
        var runner = Runner(new ThrowingFactory());
        var updates = await Drain(runner, Input());
        updates.Should().ContainSingle();
        updates[0].Should().BeOfType<ModelErrorUpdate>()
            .Which.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task RunAsync_emits_error_when_model_call_throws()
    {
        var runner = Runner(new FixedFactory(new FailingChatClient(new InvalidOperationException("network down"))));
        var updates = await Drain(runner, Input());
        updates.Should().HaveCount(2);
        updates[0].Should().BeOfType<ModelStartedUpdate>();
        updates[1].Should().BeOfType<ModelErrorUpdate>()
            .Which.Error.Should().Be("network down");
    }

    [Fact]
    public async Task RunAsync_emits_text_delta_and_final_answer_for_text_response()
    {
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [new TextContent("hello world")]),
        ])
        { FinishReason = ChatFinishReason.Stop };

        var runner = Runner(new FixedFactory(new ScriptedChatClient(response)));
        var updates = await Drain(runner, Input());

        updates.Should().HaveCount(3);
        updates[0].Should().BeOfType<ModelStartedUpdate>();
        updates[1].Should().BeOfType<ModelTextDeltaUpdate>().Which.Delta.Should().Be("hello world");
        updates[2].Should().BeOfType<ModelFinalAnswerUpdate>().Which.Text.Should().Be("hello world");
    }

    [Fact]
    public async Task RunAsync_emits_reasoning_then_final_answer()
    {
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent("thinking..."),
                new TextContent("answer"),
            ]),
        ])
        { FinishReason = ChatFinishReason.Stop };

        var runner = Runner(new FixedFactory(new ScriptedChatClient(response)));
        var updates = await Drain(runner, Input());
        updates.OfType<ModelReasoningUpdate>().Single().Text.Should().Be("thinking...");
        updates.OfType<ModelFinalAnswerUpdate>().Single().Text.Should().Be("answer");
    }

    [Fact]
    public async Task RunAsync_emits_tool_call_and_no_final_answer()
    {
        var args = new Dictionary<string, object?> { ["pattern"] = "foo" };
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "search_pattern", args)]),
        ]);

        var runner = Runner(new FixedFactory(new ScriptedChatClient(response)));
        var updates = await Drain(runner, Input());
        updates.OfType<ModelToolCallUpdate>().Should().ContainSingle()
            .Which.ToolCall.ToolName.Should().Be("search_pattern");
        updates.OfType<ModelFinalAnswerUpdate>().Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_synthesizes_a_placeholder_when_response_has_no_text_or_tool_call()
    {
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("only thinking")]),
        ])
        { FinishReason = ChatFinishReason.Length };

        var runner = Runner(new FixedFactory(new ScriptedChatClient(response)));
        var updates = await Drain(runner, Input());
        var final = updates.OfType<ModelFinalAnswerUpdate>().Single();
        final.Text.Should().Contain("no final answer");
        final.Text.Should().Contain("token budget");
    }

    [Fact]
    public async Task RunAsync_propagates_tool_results_into_messages()
    {
        var captured = new ScriptedChatClient(new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [new TextContent("ok")]),
        ]));

        var runner = Runner(new FixedFactory(captured));
        var prior = new List<ModelToolResultEntry>
        {
            new("call_1", "search_pattern", new JsonObject { ["pattern"] = "foo" }, "{\"result\":1}"),
        };
        await Drain(runner, Input(), prior);

        // System + user + assistant function-call + tool result = 4 messages.
        var messages = captured.LastMessages;
        messages.Should().NotBeNull().And.HaveCount(4);
        messages![2].Role.Should().Be(ChatRole.Assistant);
        messages[2].Contents.OfType<FunctionCallContent>().Single().CallId.Should().Be("call_1");
        messages[3].Role.Should().Be(ChatRole.Tool);
    }

    [Fact]
    public async Task RunAsync_throws_when_input_is_null()
    {
        var runner = Runner(new FixedFactory(new ScriptedChatClient(new ChatResponse([]))));
        Func<Task> act = async () =>
        {
            await foreach (var _ in runner.RunAsync(null!, [], default)) { }
        };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class ThrowingFactory : IChatClientFactory
    {
        public IChatClient Create(string deployment) => throw new InvalidOperationException("boom");
    }

    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(string deployment) => client;
    }

    /// <summary>
    /// Minimal IChatClient that returns a canned response and captures the messages it was
    /// asked to send. Streaming and metadata are unimplemented because the runner only calls
    /// <see cref="GetResponseAsync"/>.
    /// </summary>
    private sealed class ScriptedChatClient(ChatResponse response) : IChatClient
    {
        public IList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class FailingChatClient(Exception toThrow) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromException<ChatResponse>(toThrow);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
