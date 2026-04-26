using Archer.Tools.Safety;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class RepoPathResolverTests
{
    private readonly RepoPathResolver _resolver = new();

    [Fact]
    public void Allows_path_inside_root()
    {
        using var tmp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        File.WriteAllText(Path.Combine(tmp.Path, "src", "Foo.cs"), "// foo");

        _resolver.TryResolve(tmp.Path, "src/Foo.cs", out var full, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        full.Should().StartWith(tmp.Path);
    }

    [Fact]
    public void Rejects_parent_traversal()
    {
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, "../secrets.txt", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("escapes");
    }

    [Fact]
    public void Treats_leading_slash_path_as_repo_relative()
    {
        // /etc/passwd is host-absolute, but the model often types "/foo" meaning
        // "foo, relative to the repo". We strip the leading slash and combine with
        // the repo root; the path simply doesn't exist (tool will report "not found"),
        // and crucially it cannot escape the sandbox.
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, "/etc/passwd", out var full, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        full.Should().StartWith(tmp.Path);
    }

    [Fact]
    public void Allows_repo_root_itself()
    {
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, ".", out var full, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        Path.GetFullPath(full).Should().Be(Path.GetFullPath(tmp.Path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("./")]
    [InlineData("")]
    public void Treats_root_aliases_as_repo_root(string input)
    {
        using var tmp = TempDir.Create();
        _resolver.TryResolve(tmp.Path, input, out var full, out var error)
            .Should().BeTrue($"'{input}' should be normalized to repo root");
        error.Should().BeNull();
        Path.GetFullPath(full).Should().Be(Path.GetFullPath(tmp.Path));
    }

    [Fact]
    public void Leading_slash_is_stripped_to_relative()
    {
        using var tmp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        _resolver.TryResolve(tmp.Path, "/src", out var full, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        full.Should().EndWith($"{Path.DirectorySeparatorChar}src");
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }
    private TempDir(string path) { Path = path; }

    public static TempDir Create()
    {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return new TempDir(p);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* swallow */ }
    }
}
