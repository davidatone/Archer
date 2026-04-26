using System.Text.Json.Nodes;
using Archer.Domain.Tools;
using Archer.Tools;
using Archer.Tools.Safety;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Archer.Tools.Tests;

public class SearchPatternToolTests
{
    private static SearchPatternTool MakeTool() =>
        new(new RepoPathResolver(), Options.Create(new ToolOptions()));

    private static ToolRequest Req(string pattern, string repoRoot, JsonObject? extra = null)
    {
        var args = new JsonObject { ["pattern"] = pattern };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                args[kv.Key] = kv.Value?.DeepClone();
            }
        }
        return new ToolRequest(
            ToolCallId: "call_search",
            ToolName: "search_pattern",
            Arguments: args,
            RepoRoot: repoRoot,
            AgentId: "agent_TESTAGENTXXX2");
    }

    [Fact]
    public async Task Empty_pattern_fails()
    {
        using var tmp = TempDir.Create();
        var tool = MakeTool();
        var req = new ToolRequest(
            "c1",
            "search_pattern",
            new JsonObject { ["pattern"] = "" },
            tmp.Path,
            "agent_TESTAGENTXXX1");
        var r = await tool.ExecuteAsync(req);
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("pattern is required");
    }

    [Fact]
    public async Task Invalid_regex_returns_failure()
    {
        using var tmp = TempDir.Create();
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("[unclosed", tmp.Path));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("Invalid regex");
    }

    [Fact]
    public async Task Path_outside_repo_root_fails()
    {
        using var tmp = TempDir.Create();
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("foo", tmp.Path, new JsonObject { ["path"] = "../../../etc" }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Path_pointing_at_a_file_fails()
    {
        using var tmp = TempDir.Create();
        var f = Path.Combine(tmp.Path, "file.txt");
        File.WriteAllText(f, "hello");
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("hello", tmp.Path, new JsonObject { ["path"] = "file.txt" }));
        r.Success.Should().BeFalse();
        r.Summary.Should().Contain("not a directory");
    }

    [Fact]
    public async Task Finds_matches_across_files()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "public class Foo { }\nFoo bar;\n");
        File.WriteAllText(Path.Combine(tmp.Path, "b.cs"), "// nothing here\n");
        File.WriteAllText(Path.Combine(tmp.Path, "c.cs"), "var f = new Foo();\n");

        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("Foo", tmp.Path));

        r.Success.Should().BeTrue();
        r.Data["filesSearched"]!.GetValue<int>().Should().BeGreaterOrEqualTo(2);
        r.Data["filesWithMatches"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Case_sensitive_default_finds_either_case()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "x.txt"), "Hello WORLD\n");
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("hello", tmp.Path));
        r.Success.Should().BeTrue();
        r.Data["filesWithMatches"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Case_sensitive_true_misses_lowercase()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "x.txt"), "Hello\n");
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("hello", tmp.Path, new JsonObject { ["caseSensitive"] = true }));
        r.Success.Should().BeTrue();
        r.Data["filesWithMatches"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task IncludeGlobs_filters_search()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "needle\n");
        File.WriteAllText(Path.Combine(tmp.Path, "a.txt"), "needle\n");
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req(
            "needle",
            tmp.Path,
            new JsonObject { ["includeGlobs"] = new JsonArray("**/*.cs") }));
        r.Success.Should().BeTrue();
        r.Data["filesWithMatches"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task MatchCount_per_file_reports_total_and_caps_snippets()
    {
        using var tmp = TempDir.Create();
        var lines = string.Join('\n', Enumerable.Range(1, 10).Select(_ => "needle"));
        File.WriteAllText(Path.Combine(tmp.Path, "x.txt"), lines);
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req(
            "needle",
            tmp.Path,
            new JsonObject { ["maxMatchesPerFile"] = 3 }));
        r.Success.Should().BeTrue();
        var match = r.Data["matches"]!.AsArray()[0]!;
        match["matchCount"]!.GetValue<int>().Should().Be(10);
        match["snippets"]!.AsArray().Count.Should().Be(3);
    }

    [Fact]
    public async Task Snippet_lines_redact_secrets()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(
            Path.Combine(tmp.Path, "secret.txt"),
            "config api_key=ABCDEFGHIJKLMNOP1234\n");
        var tool = MakeTool();
        var r = await tool.ExecuteAsync(Req("config", tmp.Path));
        r.Success.Should().BeTrue();
        var snippet = r.Data["matches"]!.AsArray()[0]!["snippets"]!.AsArray()[0]!["text"]!.GetValue<string>();
        snippet.Should().NotContain("ABCDEFGHIJKLMNOP1234");
        snippet.Should().Contain("[redacted-secret]");
    }
}
