using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Tools.Safety;
using Microsoft.Extensions.Options;

namespace Archer.Tools;

public sealed class SearchPatternTool : ITool
{
    private readonly IRepoPathResolver _paths;
    private readonly ToolOptions _options;

    public SearchPatternTool(IRepoPathResolver paths, IOptions<ToolOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    public string Name => "search_pattern";

    public ToolDefinition Definition { get; } = new(
        Name: "search_pattern",
        Description: "Search the repository for a regex pattern across many files.",
        Parameters: JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string" },
            "path": { "type": "string" },
            "includeGlobs": { "type": "array", "items": { "type": "string" } },
            "excludeGlobs": { "type": "array", "items": { "type": "string" } },
            "caseSensitive": { "type": "boolean" },
            "maxFiles": { "type": "integer" },
            "maxMatchesPerFile": { "type": "integer" },
            "contextLines": { "type": "integer" }
          },
          "required": ["pattern"],
          "additionalProperties": false
        }
        """)!.AsObject());

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var pattern = args["pattern"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "pattern is required.", sw.Elapsed);
        }

        var relPath = args["path"]?.GetValue<string>() ?? ".";
        var caseSensitive = args["caseSensitive"]?.GetValue<bool>() ?? false;
        var maxFiles = Math.Max(1, args["maxFiles"]?.GetValue<int>() ?? 50);
        var maxMatchesPerFile = Math.Max(1, args["maxMatchesPerFile"]?.GetValue<int>() ?? 5);

        var include = ReadStringArray(args, "includeGlobs");
        var exclude = ReadStringArray(args, "excludeGlobs") ?? _options.DefaultExcludeGlobs;

        if (!_paths.TryResolve(request.RepoRoot, relPath, out var fullPath, out var error))
        {
            return ToolResult.Failed(request.ToolCallId, Name, error!, sw.Elapsed);
        }
        if (!Directory.Exists(fullPath))
        {
            return ToolResult.Failed(request.ToolCallId, Name, $"Path is not a directory: {relPath}", sw.Elapsed);
        }

        Regex regex;
        try { regex = CompilePattern(pattern, caseSensitive); }
        catch (ArgumentException ex)
        {
            return ToolResult.Failed(request.ToolCallId, Name, $"Invalid regex: {ex.Message}", sw.Elapsed);
        }

        var matcher = new GlobMatcher(fullPath, include, exclude);
        var filesSearched = 0;
        var matchesByFile = new List<(string Path, int Count, List<JsonNode> Snippets, double Score)>();

        foreach (var fileFullPath in matcher.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchesByFile.Count >= maxFiles) break;
            if (!ShouldSearch(fileFullPath)) continue;

            var lines = await TryReadLinesAsync(fileFullPath, cancellationToken);
            if (lines is null) continue;

            filesSearched++;
            var (count, snippets) = ScanLinesForPattern(lines, regex, maxMatchesPerFile);
            if (count == 0) continue;

            var rel = Path.GetRelativePath(request.RepoRoot, fileFullPath).Replace('\\', '/');
            var score = ScoreFile(rel, count, lines.Length, _options.PreferredPathPrefixes);
            matchesByFile.Add((rel, count, snippets, score));
        }

        var ordered = matchesByFile
            .OrderByDescending(m => m.Score)
            .Take(maxFiles)
            .ToArray();

        var arr = new JsonArray();
        foreach (var m in ordered)
        {
            arr.Add(new JsonObject
            {
                ["file"] = m.Path,
                ["score"] = Math.Round(m.Score, 3),
                ["matchCount"] = m.Count,
                ["snippets"] = new JsonArray([.. m.Snippets]),
            });
        }

        sw.Stop();
        var data = new JsonObject
        {
            ["pattern"] = pattern,
            ["filesSearched"] = filesSearched,
            ["filesWithMatches"] = ordered.Length,
            ["matches"] = arr,
            ["truncated"] = matchesByFile.Count > maxFiles,
        };

        var summary = $"{ordered.Length} file(s) match in {filesSearched} searched";
        return new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: Name,
            Success: true,
            Data: data,
            Summary: summary,
            ResultItemCount: ordered.Length,
            Duration: sw.Elapsed);
    }

    private static double ScoreFile(
        string relativePath,
        int matchCount,
        int totalLines,
        string[] preferredPathPrefixes)
    {
        var name = Path.GetFileName(relativePath);
        var path = relativePath.ToLowerInvariant();

        var sourcePriority = path.Contains("/test", StringComparison.Ordinal) || name.Contains("test", StringComparison.OrdinalIgnoreCase)
            ? 0.0
            : 1.0;

        var density = totalLines > 0 ? Math.Min(1.0, (double)matchCount / Math.Max(1, totalLines / 25)) : 0.0;
        var nameRelevance = name.Contains('.') ? 0.5 : 0.3;
        var pathRelevance = preferredPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? 1.0
            : 0.6;

        return
            (0.35 * nameRelevance) +
            (0.25 * pathRelevance) +
            (0.20 * density) +
            (0.10 * sourcePriority) +
            (0.10 * (1.0 / (1 + Math.Log10(1 + totalLines))));
    }

    private static Regex CompilePattern(string pattern, bool caseSensitive)
    {
        var flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!caseSensitive) flags |= RegexOptions.IgnoreCase;
        return new Regex(pattern, flags, TimeSpan.FromSeconds(2));
    }

    private bool ShouldSearch(string fileFullPath)
    {
        try
        {
            var info = new FileInfo(fileFullPath);
            if (info.Length > _options.MaxFileBytes) return false;
            if (!_options.FollowSymlinks && (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (BinaryDetector.LooksBinaryFile(fileFullPath)) return false;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string[]?> TryReadLinesAsync(string path, CancellationToken ct)
    {
        try { return await File.ReadAllLinesAsync(path, ct); }
        catch (IOException) { return null; }
    }

    private static (int Count, List<JsonNode> Snippets) ScanLinesForPattern(string[] lines, Regex regex, int maxSnippets)
    {
        var count = 0;
        var snippets = new List<JsonNode>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!regex.IsMatch(lines[i])) continue;
            count++;
            if (snippets.Count < maxSnippets)
            {
                snippets.Add(new JsonObject
                {
                    ["line"] = i + 1,
                    ["text"] = SecretRedactor.Redact(lines[i]),
                });
            }
        }
        return (count, snippets);
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
