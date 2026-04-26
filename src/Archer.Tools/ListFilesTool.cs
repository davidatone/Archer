using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Tools.Safety;
using Microsoft.Extensions.Options;

namespace Archer.Tools;

public sealed class ListFilesTool : ITool
{
    private readonly IRepoPathResolver _paths;
    private readonly ToolOptions _options;

    public ListFilesTool(IRepoPathResolver paths, IOptions<ToolOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    public string Name => "list_files";

    public ToolDefinition Definition { get; } = new(
        Name: "list_files",
        Description: "List files under a repository path.",
        Parameters: JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to repository root." },
            "recursive": { "type": "boolean" },
            "maxResults": { "type": "integer" },
            "includeGlobs": { "type": "array", "items": { "type": "string" } },
            "excludeGlobs": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """)!.AsObject());

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var relPath = args["path"]?.GetValue<string>() ?? ".";
        var recursive = args["recursive"]?.GetValue<bool>() ?? false;
        var maxResults = args["maxResults"]?.GetValue<int>() ?? 200;
        var includeGlobs = ReadStringArray(args, "includeGlobs");
        var excludeGlobs = ReadStringArray(args, "excludeGlobs") ?? _options.DefaultExcludeGlobs;

        if (!_paths.TryResolve(request.RepoRoot, relPath, out var fullPath, out var error))
        {
            return Task.FromResult(ToolResult.Failed(request.ToolCallId, Name, error!, sw.Elapsed));
        }

        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(ToolResult.Failed(
                request.ToolCallId, Name,
                $"Path is not a directory: {relPath}", sw.Elapsed));
        }

        var entries = CollectEntries(
            new CollectOptions(fullPath, recursive, maxResults, includeGlobs, excludeGlobs, request.RepoRoot),
            cancellationToken, out var truncated);

        var data = new JsonObject
        {
            ["path"] = Path.GetRelativePath(request.RepoRoot, fullPath).Replace('\\', '/'),
            ["entries"] = new JsonArray([.. entries]),
            ["truncated"] = truncated,
        };
        sw.Stop();

        var summary = $"{entries.Count} entries{(truncated ? " (truncated)" : string.Empty)} under {Path.GetRelativePath(request.RepoRoot, fullPath)}";

        return Task.FromResult(new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: Name,
            Success: true,
            Data: data,
            Summary: summary,
            ResultItemCount: entries.Count,
            Duration: sw.Elapsed));
    }

    private sealed record CollectOptions(
        string FullPath,
        bool Recursive,
        int MaxResults,
        string[]? IncludeGlobs,
        string[]? ExcludeGlobs,
        string RepoRoot);

    private List<JsonNode> CollectEntries(
        CollectOptions opts, CancellationToken cancellationToken, out bool truncated)
    {
        var entries = new List<JsonNode>();
        truncated = false;
        var searchOption = opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var matcher = new GlobMatcher(opts.FullPath, opts.IncludeGlobs, opts.ExcludeGlobs);

        foreach (var entry in Directory.EnumerateFileSystemEntries(opts.FullPath, "*", searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= opts.MaxResults)
            {
                truncated = true;
                break;
            }
            if (TryFormatEntry(entry, opts, matcher) is { } row)
            {
                entries.Add(row);
            }
        }
        return entries;
    }

    private JsonObject? TryFormatEntry(string entry, CollectOptions opts, GlobMatcher matcher)
    {
        var info = new FileInfo(entry);
        if (!_options.FollowSymlinks && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }
        if (opts.Recursive && opts.IncludeGlobs is null && opts.ExcludeGlobs is not null && !matcher.Matches(entry))
        {
            return null;
        }
        var isDir = (info.Attributes & FileAttributes.Directory) != 0;
        return new JsonObject
        {
            ["type"] = isDir ? "dir" : "file",
            ["path"] = Path.GetRelativePath(opts.RepoRoot, entry).Replace('\\', '/'),
            ["sizeBytes"] = isDir ? 0 : info.Length,
        };
    }

    private static string[]? ReadStringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr)
        {
            return null;
        }
        return arr.Where(n => n is not null).Select(n => n!.GetValue<string>()).ToArray();
    }
}
