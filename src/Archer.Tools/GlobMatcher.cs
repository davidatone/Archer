using Microsoft.Extensions.FileSystemGlobbing;

namespace Archer.Tools;

internal sealed class GlobMatcher
{
    private readonly Matcher _matcher;
    private readonly string _root;

    public GlobMatcher(
        string root,
        IEnumerable<string>? include = null,
        IEnumerable<string>? exclude = null)
    {
        _root = Path.GetFullPath(root);
        _matcher = new Matcher();

        var includeList = include?.ToArray() ?? [];
        if (includeList.Length == 0)
        {
            _matcher.AddInclude("**/*");
        }
        else
        {
            foreach (var pattern in includeList)
            {
                _matcher.AddInclude(pattern);
            }
        }

        if (exclude is not null)
        {
            foreach (var pattern in exclude)
            {
                _matcher.AddExclude(pattern);
            }
        }
    }

    public IEnumerable<string> EnumerateFiles()
    {
        var result = _matcher.GetResultsInFullPath(_root);
        return result;
    }

    public bool Matches(string fullPath)
    {
        if (!fullPath.StartsWith(_root, StringComparison.Ordinal))
        {
            return false;
        }
        var rel = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        return _matcher.Match(rel).HasMatches;
    }
}
