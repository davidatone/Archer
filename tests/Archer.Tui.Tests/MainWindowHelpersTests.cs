using Archer.Tui.Ui;
using FluentAssertions;

namespace Archer.Tui.Tests;

public class MainWindowHelpersTests
{
    [Fact]
    public void PickDefaultAgentType_returns_alphabetically_first_id()
    {
        MainWindowHelpers.PickDefaultAgentType(["zeta", "alpha", "mu"]).Should().Be("alpha");
    }

    [Fact]
    public void PickDefaultAgentType_falls_back_to_code_scout_when_empty()
    {
        MainWindowHelpers.PickDefaultAgentType([]).Should().Be("code-scout");
    }

    [Fact]
    public void PickDefaultAgentType_skips_blank_entries()
    {
        MainWindowHelpers.PickDefaultAgentType(["", "beta"]).Should().Be("beta");
    }

    [Fact]
    public void FormatTabLabel_takes_chars_6_through_11_for_long_ids()
    {
        // "agent_TESTAGENTXXX1" → indices 6..11 = "TESTAG"
        MainWindowHelpers.FormatTabLabel("agent_TESTAGENTXXX1").Should().Be(" TESTAG ");
    }

    [Fact]
    public void FormatTabLabel_short_ids_are_returned_as_is_with_padding()
    {
        MainWindowHelpers.FormatTabLabel("short").Should().Be(" short ");
    }

    [Fact]
    public void FormatTabLabel_throws_for_null_id()
    {
        Action act = () => MainWindowHelpers.FormatTabLabel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryResolveRepoPath_rejects_blank_input()
    {
        MainWindowHelpers.TryResolveRepoPath("  ", out _, out var error).Should().BeFalse();
        error.Should().Contain("required");
    }

    [Fact]
    public void TryResolveRepoPath_rejects_null_input()
    {
        MainWindowHelpers.TryResolveRepoPath(null, out _, out var error).Should().BeFalse();
        error.Should().Contain("required");
    }

    [Fact]
    public void TryResolveRepoPath_rejects_non_existent_directory()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "archer-no-such-" + Guid.NewGuid().ToString("N"));
        MainWindowHelpers.TryResolveRepoPath(bogus, out _, out var error).Should().BeFalse();
        error.Should().Contain("Not a directory");
    }

    [Fact]
    public void TryResolveRepoPath_returns_canonical_full_path()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "archer-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            MainWindowHelpers.TryResolveRepoPath(tmp, out var resolved, out var error).Should().BeTrue();
            resolved.Should().Be(Path.GetFullPath(tmp));
            error.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(tmp); } catch { /* swallow */ }
        }
    }
}
