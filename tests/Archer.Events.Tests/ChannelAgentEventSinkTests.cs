using Archer.Domain.Events;
using Archer.Events;
using FluentAssertions;

namespace Archer.Events.Tests;

public class ChannelAgentEventSinkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SubscribeAsync_yields_published_events_for_matching_agent()
    {
        await using var sink = new ChannelAgentEventSink();
        using var cts = new CancellationTokenSource(TestTimeout);

        var collector = Task.Run(async () =>
        {
            var events = new List<AgentEvent>();
            await foreach (var e in sink.SubscribeAsync("agent_AAA", cts.Token))
            {
                events.Add(e);
                if (events.Count == 2) break;
            }
            return events;
        }, cts.Token);

        await Task.Delay(50, cts.Token);
        await sink.PublishAsync(new SummaryEvent
        {
            AgentId = "agent_AAA", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "first",
        }, cts.Token);
        await sink.PublishAsync(new SummaryEvent
        {
            AgentId = "agent_AAA", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "second",
        }, cts.Token);

        var got = await collector;
        got.Should().HaveCount(2);
        got[0].Should().BeOfType<SummaryEvent>().Which.Message.Should().Be("first");
        got[1].Should().BeOfType<SummaryEvent>().Which.Message.Should().Be("second");
    }

    [Fact]
    public async Task SubscribeAsync_isolates_events_per_agent_id()
    {
        await using var sink = new ChannelAgentEventSink();
        using var cts = new CancellationTokenSource(TestTimeout);

        var aTask = Task.Run(async () =>
        {
            await foreach (var e in sink.SubscribeAsync("agent_AAAAAAAAAAAA", cts.Token))
            {
                return e;
            }
            return null!;
        });

        await Task.Delay(50, cts.Token);
        await sink.PublishAsync(new SummaryEvent
        {
            AgentId = "agent_BBBBBBBBBBBB", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "for-B",
        }, cts.Token);
        await sink.PublishAsync(new SummaryEvent
        {
            AgentId = "agent_AAAAAAAAAAAA", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "for-A",
        }, cts.Token);

        var first = await aTask;
        first.Should().BeOfType<SummaryEvent>().Which.Message.Should().Be("for-A");
    }

    [Fact]
    public async Task SubscribeAsync_supports_multiple_subscribers_per_agent()
    {
        await using var sink = new ChannelAgentEventSink();
        using var cts = new CancellationTokenSource(TestTimeout);

        async Task<List<AgentEvent>> Collect()
        {
            var events = new List<AgentEvent>();
            await foreach (var e in sink.SubscribeAsync("agent_AAA", cts.Token))
            {
                events.Add(e);
                if (events.Count == 1) break;
            }
            return events;
        }

        var sub1 = Task.Run(Collect);
        var sub2 = Task.Run(Collect);

        await Task.Delay(80, cts.Token);
        await sink.PublishAsync(new SummaryEvent
        {
            AgentId = "agent_AAA", TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, Message = "broadcast",
        }, cts.Token);

        var (a, b) = (await sub1, await sub2);
        a.Should().HaveCount(1);
        b.Should().HaveCount(1);
        a[0].Should().BeOfType<SummaryEvent>().Which.Message.Should().Be("broadcast");
    }

    [Fact]
    public async Task SubscribeAsync_propagates_cancellation_to_consumer()
    {
        await using var sink = new ChannelAgentEventSink();
        using var cts = new CancellationTokenSource();

        var subscriber = Task.Run(async () =>
        {
            await foreach (var _ in sink.SubscribeAsync("agent_X", cts.Token))
            {
                // never reached
            }
        });

        cts.Cancel();
        // Cancellation throws OperationCanceledException out of the iterator — the contract is
        // "the consumer terminates promptly", not "completes successfully".
        var act = async () => await subscriber.WaitAsync(TestTimeout);
        await act.Should().ThrowAsync<OperationCanceledException>();
        subscriber.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_throws_when_event_is_null()
    {
        await using var sink = new ChannelAgentEventSink();
        var act = async () => await sink.PublishAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DisposeAsync_completes_writers_and_terminates_subscribers()
    {
        var sink = new ChannelAgentEventSink();
        using var cts = new CancellationTokenSource(TestTimeout);

        var subscriber = Task.Run(async () =>
        {
            await foreach (var _ in sink.SubscribeAsync("agent_X", cts.Token))
            {
                // expected: nothing arrives, but the iterator must exit when the writer completes
            }
        });

        await Task.Delay(50, cts.Token);
        await sink.DisposeAsync();

        await subscriber.WaitAsync(TestTimeout);
        subscriber.IsCompletedSuccessfully.Should().BeTrue();
    }
}
