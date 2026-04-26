namespace Archer.Tools.Safety;

internal static class BinaryDetector
{
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        // Treat as binary if any NUL byte appears, or non-printable bytes exceed 30%.
        if (sample.IsEmpty)
        {
            return false;
        }
        var nonPrintable = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            var b = sample[i];
            if (b == 0)
            {
                return true;
            }
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
            {
                nonPrintable++;
            }
        }
        return nonPrintable * 10 > sample.Length * 3;
    }

    public static bool LooksBinaryFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))];
            var read = stream.Read(buffer);
            return LooksBinary(buffer[..read]);
        }
        catch (IOException)
        {
            return true;
        }
    }
}
