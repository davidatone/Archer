using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Tools.Safety;
using Microsoft.Extensions.Options;

namespace Archer.Tools;

public sealed class GrepTool : ITool
{
    private readonly IRepoPathResolver _paths;
    private readonly ToolOptions _options;

    public GrepTool(IRepoPathResolver paths, IOptions<ToolOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    public string Name => "grep";

    public ToolDefinition Definition { get; } = new(
        Name: "grep",
        Description: "Search for a regex pattern inside one file and return matching lines with context.",
        Parameters: JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "file": { "type": "string", "description": "File path relative to repository root." },
            "pattern": { "type": "string", "description": "Regex pattern." },
            "caseSensitive": { "type": "boolean" },
            "contextLines": { "type": "integer" },
            "maxMatches": { "type": "integer" }
          },
          "required": ["file", "pattern"],
          "additionalProperties": false
        }
        """)!.AsObject());

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var file = args["file"]?.GetValue<string>();
        var pattern = args["pattern"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(pattern))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "file and pattern are required.", sw.Elapsed);
        }

        var fileFailure = ResolveTargetFile(request, file, sw, out var fullPath);
        if (fileFailure is not null) return fileFailure;

        var caseSensitive = args["caseSensitive"]?.GetValue<bool>() ?? false;
        var contextLines = Math.Max(0, args["contextLines"]?.GetValue<int>() ?? 2);
        var maxMatches = Math.Max(1, args["maxMatches"]?.GetValue<int>() ?? 50);

        var regexFailure = CompileRegex(pattern, caseSensitive, request, sw, out var regex);
        if (regexFailure is not null) return regexFailure;

        var lines = await File.ReadAllLinesAsync(fullPath!, cancellationToken);
        var matches = new List<JsonNode>();
        var truncated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!regex.IsMatch(lines[i]))
            {
                continue;
            }

            if (matches.Count >= maxMatches)
            {
                truncated = true;
                break;
            }

            var before = new JsonArray();
            for (var b = Math.Max(0, i - contextLines); b < i; b++)
            {
                before.Add(SecretRedactor.Redact(lines[b]));
            }
            var after = new JsonArray();
            for (var a = i + 1; a <= Math.Min(lines.Length - 1, i + contextLines); a++)
            {
                after.Add(SecretRedactor.Redact(lines[a]));
            }

            matches.Add(new JsonObject
            {
                ["line"] = i + 1,
                ["text"] = SecretRedactor.Redact(lines[i]),
                ["before"] = before,
                ["after"] = after,
            });
        }

        sw.Stop();
        var data = new JsonObject
        {
            ["file"] = Path.GetRelativePath(request.RepoRoot, fullPath!).Replace('\\', '/'),
            ["matches"] = new JsonArray([.. matches]),
            ["truncated"] = truncated,
        };
        var summary = $"{matches.Count} match(es){(truncated ? " (truncated)" : string.Empty)} in {file}";
        return new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: Name,
            Success: true,
            Data: data,
            Summary: summary,
            ResultItemCount: matches.Count,
            Duration: sw.Elapsed);
    }

    private ToolResult? ResolveTargetFile(ToolRequest request, string file, Stopwatch sw, out string? fullPath)
    {
        if (!_paths.TryResolve(request.RepoRoot, file, out fullPath, out var error))
        {
            return ToolResult.Failed(request.ToolCallId, Name, error!, sw.Elapsed);
        }
        if (!File.Exists(fullPath))
        {
            return ToolResult.Failed(request.ToolCallId, Name, $"File not found: {file}", sw.Elapsed);
        }
        var info = new FileInfo(fullPath);
        if (info.Length > _options.MaxFileBytes)
        {
            return ToolResult.Failed(
                request.ToolCallId, Name,
                $"File too large ({info.Length} bytes > {_options.MaxFileBytes}).",
                sw.Elapsed);
        }
        if (BinaryDetector.LooksBinaryFile(fullPath))
        {
            return ToolResult.Failed(request.ToolCallId, Name, "Refusing to grep a binary file.", sw.Elapsed);
        }
        return null;
    }

    private ToolResult? CompileRegex(string pattern, bool caseSensitive, ToolRequest request, Stopwatch sw, out Regex regex)
    {
        var flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!caseSensitive) flags |= RegexOptions.IgnoreCase;
        try
        {
            regex = new Regex(pattern, flags, TimeSpan.FromSeconds(2));
            return null;
        }
        catch (ArgumentException ex)
        {
            regex = null!;
            return ToolResult.Failed(request.ToolCallId, Name, $"Invalid regex: {ex.Message}", sw.Elapsed);
        }
    }
}
