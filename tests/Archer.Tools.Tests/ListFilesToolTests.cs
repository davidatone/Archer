using System.Text.Json.Nodes;
using Archer.Domain.Tools;
using Archer.Tools;
using Archer.Tools.Safety;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Archer.Tools.Tests;

public class ListFilesToolTests
{
    [Fact]
    public async Task Lists_top_level_files()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "// a");
        File.WriteAllText(Path.Combine(tmp.Path, "b.cs"), "// b");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "sub"));

        var tool = new ListFilesTool(new RepoPathResolver(), Options.Create(new ToolOptions()));
        var req = new ToolRequest(
            ToolCallId: "call_1",
            ToolName: "list_files",
            Arguments: new JsonObject { ["path"] = "." },
            RepoRoot: tmp.Path,
            AgentId: "scout_TESTAGENTXXX1");

        var result = await tool.ExecuteAsync(req, CancellationToken.None);
        result.Success.Should().BeTrue();

        var entries = result.Data["entries"]!.AsArray();
        entries.Count.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task Rejects_path_escape()
    {
        using var tmp = TempDir.Create();
        var tool = new ListFilesTool(new RepoPathResolver(), Options.Create(new ToolOptions()));
        var req = new ToolRequest(
            ToolCallId: "call_2",
            ToolName: "list_files",
            Arguments: new JsonObject { ["path"] = "../" },
            RepoRoot: tmp.Path,
            AgentId: "scout_TESTAGENTXXX2");
        var result = await tool.ExecuteAsync(req, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
