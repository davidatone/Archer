using Archer.Persistence.Agents;
using FluentAssertions;

namespace Archer.Persistence.Tests;

public sealed class AgentDefinitionRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string _dir1;
    private readonly string _dir2;

    public AgentDefinitionRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-agentreg-" + Guid.NewGuid().ToString("N"));
        _dir1 = Path.Combine(_root, "dir1");
        _dir2 = Path.Combine(_root, "dir2");
        Directory.CreateDirectory(_dir1);
        Directory.CreateDirectory(_dir2);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initial_scan_loads_definitions_from_all_directories()
    {
        File.WriteAllText(Path.Combine(_dir1, "a.yaml"), MinimalYaml("agent-a"));
        File.WriteAllText(Path.Combine(_dir2, "b.yaml"), MinimalYaml("agent-b"));

        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1, _dir2]);

        reg.All.Should().HaveCount(2);
        reg.Get("agent-a").Should().NotBeNull();
        reg.Get("agent-b").Should().NotBeNull();
    }

    [Fact]
    public void First_directory_takes_precedence_on_id_collision()
    {
        File.WriteAllText(Path.Combine(_dir1, "agent-x.yaml"), MinimalYaml("agent-x", desc: "dir1-version"));
        File.WriteAllText(Path.Combine(_dir2, "agent-x.yaml"), MinimalYaml("agent-x", desc: "dir2-version"));

        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1, _dir2]);

        reg.Get("agent-x")!.Description.Should().Be("dir1-version");
    }

    [Fact]
    public void Missing_directory_is_skipped_silently()
    {
        File.WriteAllText(Path.Combine(_dir1, "a.yaml"), MinimalYaml("agent-a"));

        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1, "/nonexistent/path/zzz"]);

        reg.All.Should().HaveCount(1);
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1]);
        reg.Get("never-registered").Should().BeNull();
    }

    [Fact]
    public void Get_returns_null_for_null_id()
    {
        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1]);
        reg.Get(null!).Should().BeNull();
    }

    [Fact]
    public async Task Adding_a_yaml_at_runtime_loads_via_file_watcher()
    {
        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1]);
        var changed = false;
        reg.Changed += () => changed = true;

        File.WriteAllText(Path.Combine(_dir1, "new-agent.yaml"), MinimalYaml("new-agent"));

        // Wait for the debounced reload (250ms debounce + slack).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && reg.Get("new-agent") is null)
        {
            await Task.Delay(50);
        }

        reg.Get("new-agent").Should().NotBeNull();
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_yaml_at_runtime_unloads_via_file_watcher()
    {
        var path = Path.Combine(_dir1, "to-remove.yaml");
        File.WriteAllText(path, MinimalYaml("to-remove"));

        using var reg = AgentDefinitionRegistry.FromDirectories([_dir1]);
        reg.Get("to-remove").Should().NotBeNull();

        File.Delete(path);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && reg.Get("to-remove") is not null)
        {
            await Task.Delay(50);
        }
        reg.Get("to-remove").Should().BeNull();
    }

    private static string MinimalYaml(string id, string desc = "") => $"""
        id: {id}
        description: {desc}
        instructions: instructions for {id}
        tools:
          - list_files
        model:
          deployment: gpt-x
        """;
}
