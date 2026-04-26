using System.Text.Json.Nodes;
using Archer.Domain.Tools;
using Archer.Tools;
using Archer.Tools.Safety;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Archer.Tools.Tests;

public class GrepToolTests
{
    [Fact]
    public async Task Finds_matches_with_context()
    {
        using var tmp = TempDir.Create();
        var file = Path.Combine(tmp.Path, "auth.cs");
        File.WriteAllLines(file, [
            "public class AuthService {",
            "  public bool Validate(string token) {",
            "    return ValidateToken(token);",
            "  }",
            "}",
        ]);

        var tool = new GrepTool(new RepoPathResolver(), Options.Create(new ToolOptions()));
        var req = new ToolRequest(
            ToolCallId: "call_g1",
            ToolName: "grep",
            Arguments: new JsonObject
            {
                ["file"] = "auth.cs",
                ["pattern"] = "ValidateToken",
            },
            RepoRoot: tmp.Path,
            AgentId: "scout_TESTAGENTXXX3");

        var result = await tool.ExecuteAsync(req, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.Data["matches"]!.AsArray().Count.Should().Be(1);
    }
}
