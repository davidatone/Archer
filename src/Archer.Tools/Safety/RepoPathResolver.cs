using Archer.Application.Tools;

namespace Archer.Tools.Safety;

public sealed class RepoPathResolver : IRepoPathResolver
{
    public string Resolve(string repoRoot, string relativePath)
    {
        if (!TryResolve(repoRoot, relativePath, out var fullPath, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return fullPath;
    }

    public bool TryResolve(string repoRoot, string relativePath, out string fullPath, out string? error)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            fullPath = string.Empty;
            error = "Repository root is empty.";
            return false;
        }

        var normalizedRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var rawRelative = NormalizeRelative(relativePath);

        // After normalization, "" and "." both mean "repo root". Anything still rooted is an
        // attempt to escape the sandbox.
        if (rawRelative.Length > 0 && Path.IsPathRooted(rawRelative))
        {
            fullPath = string.Empty;
            error = "Absolute paths are not allowed; provide a path relative to the repository root.";
            return false;
        }

        // Empty/root → return canonical root without trailing separator. Anything else → combine and canonicalize.
        var rootNoSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        var candidate = rawRelative.Length == 0
            ? rootNoSep
            : Path.GetFullPath(Path.Combine(normalizedRoot, rawRelative));
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(normalizedCandidate, rootNoSep, StringComparison.Ordinal)
            && !candidate.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            fullPath = string.Empty;
            error = "Path escapes repository root.";
            return false;
        }

        fullPath = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// Coerce model-friendly path forms into something we can combine with the repo root:
    ///   ""       → ""    (repo root)
    ///   "."      → ""
    ///   "./"     → ""
    ///   "/"      → ""    (POSIX "root of the project", as the model often phrases it)
    ///   "./src"  → "src"
    ///   "/src"   → "src" (treat any leading slash as "from repo root")
    ///
    /// The escape-detection at the bottom of TryResolve still catches "..", drive letters,
    /// and other sneaky inputs that would land outside the repo.
    /// </summary>
    private static string NormalizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var trimmed = relativePath.Trim();
        if (trimmed is "." or "./" or ".\\" or "/" or "\\")
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("./", StringComparison.Ordinal) ||
            trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            trimmed = trimmed.TrimStart('/', '\\');
        }

        return trimmed;
    }
}
