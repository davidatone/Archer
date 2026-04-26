using Archer.Tools.Safety;
using FluentAssertions;

namespace Archer.Tools.Tests;

public class BinaryDetectorTests
{
    [Fact]
    public void Empty_sample_is_not_binary()
    {
        BinaryDetector.LooksBinary(ReadOnlySpan<byte>.Empty).Should().BeFalse();
    }

    [Fact]
    public void Plain_ascii_text_is_not_binary()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello world\n");
        BinaryDetector.LooksBinary(bytes).Should().BeFalse();
    }

    [Fact]
    public void Sample_with_nul_byte_is_binary()
    {
        Span<byte> bytes = stackalloc byte[] { (byte)'a', 0x00, (byte)'b' };
        BinaryDetector.LooksBinary(bytes).Should().BeTrue();
    }

    [Fact]
    public void High_density_of_control_bytes_is_binary()
    {
        // 4 control bytes out of 10 = 40% > 30% threshold
        Span<byte> bytes = stackalloc byte[] { (byte)'a', 0x01, 0x02, (byte)'b', 0x03, 0x04, (byte)'c', (byte)'d', (byte)'e', (byte)'f' };
        BinaryDetector.LooksBinary(bytes).Should().BeTrue();
    }

    [Fact]
    public void Tab_newline_carriage_return_and_escape_count_as_printable()
    {
        Span<byte> bytes = stackalloc byte[] { (byte)'a', 0x09, 0x0A, 0x0D, 0x1B, (byte)'b' };
        BinaryDetector.LooksBinary(bytes).Should().BeFalse();
    }

    [Fact]
    public void LooksBinaryFile_returns_true_for_nul_padded_binary()
    {
        var path = Path.Combine(Path.GetTempPath(), "archer-binary-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x00, (byte)'a']);
            BinaryDetector.LooksBinaryFile(path).Should().BeTrue();
        }
        finally
        {
            try { File.Delete(path); } catch { /* swallow */ }
        }
    }

    [Fact]
    public void LooksBinaryFile_returns_false_for_text_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "archer-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "plain text content\nwith newlines\n");
            BinaryDetector.LooksBinaryFile(path).Should().BeFalse();
        }
        finally
        {
            try { File.Delete(path); } catch { /* swallow */ }
        }
    }

    [Fact]
    public void LooksBinaryFile_returns_true_for_missing_or_unreadable_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "archer-missing-" + Guid.NewGuid().ToString("N"));
        // Conservatively reports binary on any IO error; the search tools then skip the file.
        BinaryDetector.LooksBinaryFile(path).Should().BeTrue();
    }
}
