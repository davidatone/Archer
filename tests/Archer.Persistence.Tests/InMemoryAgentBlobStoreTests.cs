using Archer.Persistence;
using FluentAssertions;

namespace Archer.Persistence.Tests;

public class InMemoryAgentBlobStoreTests
{
    public sealed class Payload
    {
        public string Title { get; set; } = "";
        public int Count { get; set; }
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips()
    {
        var store = new InMemoryAgentBlobStore();
        await store.SaveAsync("agent_X", "blob1", new Payload { Title = "hello", Count = 42 });

        var loaded = await store.LoadAsync<Payload>("agent_X", "blob1");

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("hello");
        loaded.Count.Should().Be(42);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_unknown()
    {
        var store = new InMemoryAgentBlobStore();
        var loaded = await store.LoadAsync<Payload>("agent_X", "missing");
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_removes_existing_blob()
    {
        var store = new InMemoryAgentBlobStore();
        await store.SaveAsync("agent_X", "blob1", new Payload { Title = "x" });
        await store.DeleteAsync("agent_X", "blob1");

        (await store.LoadAsync<Payload>("agent_X", "blob1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_for_unknown_blob()
    {
        var store = new InMemoryAgentBlobStore();
        var act = async () => await store.DeleteAsync("agent_X", "never-saved");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_blob()
    {
        var store = new InMemoryAgentBlobStore();
        await store.SaveAsync("agent_X", "blob1", new Payload { Title = "v1" });
        await store.SaveAsync("agent_X", "blob1", new Payload { Title = "v2" });

        var loaded = await store.LoadAsync<Payload>("agent_X", "blob1");
        loaded!.Title.Should().Be("v2");
    }

    [Fact]
    public async Task Blobs_are_isolated_per_agent_and_per_name()
    {
        var store = new InMemoryAgentBlobStore();
        await store.SaveAsync("agent_A", "x", new Payload { Title = "a/x" });
        await store.SaveAsync("agent_B", "x", new Payload { Title = "b/x" });
        await store.SaveAsync("agent_A", "y", new Payload { Title = "a/y" });

        (await store.LoadAsync<Payload>("agent_A", "x"))!.Title.Should().Be("a/x");
        (await store.LoadAsync<Payload>("agent_B", "x"))!.Title.Should().Be("b/x");
        (await store.LoadAsync<Payload>("agent_A", "y"))!.Title.Should().Be("a/y");
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_throw_for_null_agentId()
    {
        var store = new InMemoryAgentBlobStore();
        await ((Func<Task>)(() => store.SaveAsync<Payload>(null!, "b", new Payload()))).Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => store.LoadAsync<Payload>(null!, "b"))).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_throws_for_null_data()
    {
        var store = new InMemoryAgentBlobStore();
        var act = async () => await store.SaveAsync<Payload>("a", "b", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
