using Archer.Tools.Safety;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class RepoPathResolverExtraTests
{
    private readonly RepoPathResolver _resolver = new();

    [Fact]
    public void Resolve_throws_when_repo_root_is_empty()
    {
        Action act = () => _resolver.Resolve("", "x");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Repository root is empty*");
    }

    [Fact]
    public void TryResolve_returns_false_when_repo_root_is_empty()
    {
        _resolver.TryResolve("", "x", out var full, out var error).Should().BeFalse();
        full.Should().BeEmpty();
        error.Should().Contain("Repository root is empty");
    }

    [Fact]
    public void TryResolve_normalizes_unix_absolute_paths_into_repo_relative()
    {
        // The resolver intentionally rewrites a leading slash to a repo-relative path
        // (so the model can say "/etc/foo" meaning "from the repo root"). Actual escapes
        // via ".." are caught instead — covered by Rejects_parent_traversal in the
        // sibling test class.
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, "/etc/passwd", out var full, out var error).Should().BeTrue();
        error.Should().BeNull();
        full.Should().StartWith(tmp.Path);
    }

    [Fact]
    public void Resolve_returns_full_path_for_simple_relative()
    {
        using var tmp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        var full = _resolver.Resolve(tmp.Path, "src");
        full.Should().StartWith(tmp.Path);
        full.Should().EndWith("src");
    }

    [Theory]
    [InlineData("./src", "src")]
    [InlineData(".\\src", "src")]
    [InlineData("\\src", "src")]
    public void NormalizeRelative_strips_dot_and_backslash_prefixes(string input, string expectedSuffix)
    {
        using var tmp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        _resolver.TryResolve(tmp.Path, input, out var full, out var error).Should().BeTrue();
        error.Should().BeNull();
        full.Should().EndWith(expectedSuffix);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".\\")]
    [InlineData("/")]
    [InlineData("\\")]
    public void NormalizeRelative_treats_root_aliases_as_repo_root(string input)
    {
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, input, out var full, out _).Should().BeTrue();
        Path.GetFullPath(full).Should().Be(Path.GetFullPath(tmp.Path));
    }
}
