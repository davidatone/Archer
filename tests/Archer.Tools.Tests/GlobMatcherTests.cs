using Archer.Tools;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class GlobMatcherTests
{
    [Fact]
    public void EnumerateFiles_includes_all_when_include_is_null()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "");
        File.WriteAllText(Path.Combine(tmp.Path, "b.txt"), "");

        var m = new GlobMatcher(tmp.Path);
        var files = m.EnumerateFiles().Select(Path.GetFileName).ToHashSet();
        files.Should().BeEquivalentTo("a.cs", "b.txt");
    }

    [Fact]
    public void EnumerateFiles_respects_explicit_include_patterns()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "");
        File.WriteAllText(Path.Combine(tmp.Path, "b.txt"), "");

        var m = new GlobMatcher(tmp.Path, include: ["**/*.cs"]);
        m.EnumerateFiles().Select(Path.GetFileName).Should().BeEquivalentTo("a.cs");
    }

    [Fact]
    public void EnumerateFiles_respects_exclude_patterns()
    {
        using var tmp = TempDir.Create();
        File.WriteAllText(Path.Combine(tmp.Path, "a.cs"), "");
        File.WriteAllText(Path.Combine(tmp.Path, "b.cs"), "");

        var m = new GlobMatcher(tmp.Path, include: ["**/*.cs"], exclude: ["b.cs"]);
        m.EnumerateFiles().Select(Path.GetFileName).Should().BeEquivalentTo("a.cs");
    }

    [Fact]
    public void Matches_returns_true_for_path_under_root_that_satisfies_pattern()
    {
        using var tmp = TempDir.Create();
        var path = Path.Combine(tmp.Path, "src", "Foo.cs");
        var m = new GlobMatcher(tmp.Path, include: ["**/*.cs"]);
        m.Matches(path).Should().BeTrue();
    }

    [Fact]
    public void Matches_returns_false_for_path_outside_root()
    {
        using var tmp = TempDir.Create();
        var m = new GlobMatcher(tmp.Path, include: ["**/*"]);
        m.Matches("/elsewhere/Foo.cs").Should().BeFalse();
    }

    [Fact]
    public void Matches_uses_forward_slashes_internally_so_windows_paths_normalize()
    {
        using var tmp = TempDir.Create();
        var path = Path.Combine(tmp.Path, "src", "Foo.cs");
        var m = new GlobMatcher(tmp.Path, include: ["src/**"]);
        m.Matches(path).Should().BeTrue();
    }
}
