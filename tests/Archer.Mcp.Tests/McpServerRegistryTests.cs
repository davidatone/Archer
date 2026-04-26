using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Registry;
using FluentAssertions;

namespace Archer.Mcp.Tests;

public sealed class McpServerRegistryTests : IDisposable
{
    private readonly string _repoDir;
    private readonly string _userDir;

    public McpServerRegistryTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "archer-mcp-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(root, "repo-mcp");
        _userDir = Path.Combine(root, "user-mcp");
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_userDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_repoDir)!, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Initial_scan_loads_yaml_from_both_directories()
    {
        File.WriteAllText(Path.Combine(_repoDir, "simplemem.yaml"), MinimalYaml("simplemem"));
        File.WriteAllText(Path.Combine(_userDir, "trello.yaml"), MinimalYaml("trello"));

        using var reg = McpServerRegistry.FromDirectories([_repoDir, _userDir], _userDir);

        reg.All.Should().HaveCount(2);
        reg.Get("simplemem").Should().NotBeNull();
        reg.Get("trello").Should().NotBeNull();
    }

    [Fact]
    public void Repo_directory_takes_precedence_over_user_directory_for_collisions()
    {
        File.WriteAllText(Path.Combine(_repoDir, "trello.yaml"), MinimalYaml("trello", description: "from repo"));
        File.WriteAllText(Path.Combine(_userDir, "trello.yaml"), MinimalYaml("trello", description: "from user"));

        using var reg = McpServerRegistry.FromDirectories([_repoDir, _userDir], _userDir);

        reg.Get("trello")!.Description.Should().Be("from repo");
    }

    [Fact]
    public async Task AddOrUpdateAsync_persists_yaml_and_registry_reflects_immediately()
    {
        using var reg = McpServerRegistry.FromDirectories([_repoDir, _userDir], _userDir);
        var changeFired = false;
        McpRegistryChange? captured = null;
        reg.Changed += c => { changeFired = true; captured = c; };

        var config = new McpServerConfig
        {
            Name = "trello",
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Sse,
                Endpoint = new Uri("http://trello-mcp:8000/sse"),
            },
            Auth = new McpAuthConfig { Type = McpAuthType.ApiKey },
        };

        await reg.AddOrUpdateAsync(config);

        reg.Get("trello").Should().NotBeNull();
        reg.Get("trello")!.Auth.Type.Should().Be(McpAuthType.ApiKey);
        File.Exists(Path.Combine(_userDir, "trello.yaml")).Should().BeTrue();
        changeFired.Should().BeTrue();
        captured!.Kind.Should().Be(McpRegistryChangeKind.Added);
    }

    [Fact]
    public async Task RemoveAsync_deletes_user_yaml_and_clears_registry()
    {
        using var reg = McpServerRegistry.FromDirectories([_repoDir, _userDir], _userDir);
        await reg.AddOrUpdateAsync(new McpServerConfig
        {
            Name = "trello",
            Transport = new McpTransportConfig { Type = McpTransportType.Stdio, Command = "x" },
        });

        var removed = await reg.RemoveAsync("trello");

        removed.Should().BeTrue();
        reg.Get("trello").Should().BeNull();
        File.Exists(Path.Combine(_userDir, "trello.yaml")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_refuses_to_remove_repo_level_yaml()
    {
        File.WriteAllText(Path.Combine(_repoDir, "simplemem.yaml"), MinimalYaml("simplemem"));
        using var reg = McpServerRegistry.FromDirectories([_repoDir, _userDir], _userDir);

        var removed = await reg.RemoveAsync("simplemem");

        removed.Should().BeFalse();
        reg.Get("simplemem").Should().NotBeNull();
        File.Exists(Path.Combine(_repoDir, "simplemem.yaml")).Should().BeTrue();
    }

    private static string MinimalYaml(string name, string description = "") => $"""
        name: {name}
        description: {description}
        transport:
          type: streamable-http
          endpoint: http://{name}:8000/mcp
        """;
}
