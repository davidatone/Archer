using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archer.Persistence.Tests;

public class FileAgentStateStoreTests
{
    [Fact]
    public async Task Saves_and_loads_state()
    {
        using var tmp = TempDir.Create();
        var store = NewStore(tmp.Path);
        var agentId = AgentId.New();

        var state = new AgentState
        {
            AgentId = agentId,
            AgentDefinitionId = "code-scout",
            RepoRoot = tmp.Path,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LatestMessageSeq = 1,
        };
        state.Messages.Add(new AgentMessage
        {
            Seq = 1, Role = MessageRole.User, Content = "hi", CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await store.SaveAsync(state);

        var loaded = await store.LoadAsync(agentId);
        loaded.Should().NotBeNull();
        loaded!.AgentId.Should().Be(agentId);
        loaded.Messages.Should().HaveCount(1);
        loaded.Messages[0].Content.Should().Be("hi");
    }

    [Fact]
    public async Task Lists_known_agents()
    {
        using var tmp = TempDir.Create();
        var store = NewStore(tmp.Path);
        var ids = new[] { AgentId.New(), AgentId.New(), AgentId.New() };
        foreach (var id in ids)
        {
            await store.SaveAsync(new AgentState
            {
                AgentId = id,
                AgentDefinitionId = "code-scout",
                RepoRoot = tmp.Path,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LatestMessageSeq = 0,
            });
        }

        var list = await store.ListAgentsAsync();
        list.Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task Appends_event_to_ndjson_log()
    {
        using var tmp = TempDir.Create();
        var store = NewStore(tmp.Path);
        var agentId = AgentId.New();

        await store.SaveAsync(new AgentState
        {
            AgentId = agentId,
            AgentDefinitionId = "code-scout",
            RepoRoot = tmp.Path,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LatestMessageSeq = 0,
        });

        await store.AppendEventAsync(agentId, new SummaryEvent
        {
            AgentId = agentId,
            TurnId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Message = "made progress",
        });

        var log = Path.Combine(tmp.Path, "agents", agentId, "events.ndjson");
        File.Exists(log).Should().BeTrue();
        var lines = File.ReadAllLines(log);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("made progress");
    }

    private static FileAgentStateStore NewStore(string root) =>
        new(Options.Create(new FileAgentStateStoreOptions { StateDirectory = root }), NullLogger<FileAgentStateStore>.Instance);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }
    private TempDir(string path) { Path = path; }

    public static TempDir Create()
    {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archer-persist-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return new TempDir(p);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* swallow */ }
    }
}
