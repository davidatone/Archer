using System.ClientModel.Primitives;
using Archer.Model.AgentFramework;
using FluentAssertions;

namespace Archer.Model.Tests;

public class AzureApiKeyHeaderPolicyTests
{
    [Fact]
    public void Process_writes_api_key_header_and_invokes_next()
    {
        var policy = new AzureApiKeyHeaderPolicy("the-key");
        var capture = new CapturingPolicy();
        var pipeline = new List<PipelinePolicy> { policy, capture };
        var message = TestPipeline.NewMessage();

        policy.Process(message, pipeline, currentIndex: 0);

        message.Request.Headers.TryGetValue("api-key", out var value).Should().BeTrue();
        value.Should().Be("the-key");
        capture.WasInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_writes_api_key_header_and_invokes_next()
    {
        var policy = new AzureApiKeyHeaderPolicy("async-key");
        var capture = new CapturingPolicy();
        var pipeline = new List<PipelinePolicy> { policy, capture };
        var message = TestPipeline.NewMessage();

        await policy.ProcessAsync(message, pipeline, currentIndex: 0);

        message.Request.Headers.TryGetValue("api-key", out var value).Should().BeTrue();
        value.Should().Be("async-key");
        capture.WasInvokedAsync.Should().BeTrue();
    }

    private sealed class CapturingPolicy : PipelinePolicy
    {
        public bool WasInvoked { get; private set; }
        public bool WasInvokedAsync { get; private set; }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasInvoked = true;
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasInvokedAsync = true;
            return ValueTask.CompletedTask;
        }
    }

    private static class TestPipeline
    {
        public static PipelineMessage NewMessage()
        {
            // ClientPipeline.Create() builds a real pipeline that exposes a CreateMessage helper
            // — we don't actually send through it, so an empty-options pipeline is fine.
            var pipeline = ClientPipeline.Create(new ClientPipelineOptions());
            return pipeline.CreateMessage();
        }
    }
}
