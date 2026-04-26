using System.Text.Json;
using System.Text.Json.Nodes;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Mcp.Client;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Archer.Mcp.Tools;

/// <summary>
/// Adapts one MCP tool (advertised by a connected MCP server) into Archer's <see cref="ITool"/>
/// surface. The wire name is <c>{server}__{toolName}</c>; logical name (with dot) is what
/// appears in agent YAML. Dispatches via the shared <see cref="IMcpClientPool"/> so
/// reconnects are transparent.
/// </summary>
public sealed class McpToolAdapter : ITool
{
    private readonly IMcpClientPool _pool;
    private readonly string _serverName;
    private readonly string _serverToolName;
    private readonly ILogger<McpToolAdapter>? _logger;

    public McpToolAdapter(
        IMcpClientPool pool,
        string serverName,
        string serverToolName,
        string description,
        JsonObject parameters,
        ILogger<McpToolAdapter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(serverToolName);
        ArgumentNullException.ThrowIfNull(parameters);
        _pool = pool;
        _serverName = serverName;
        _serverToolName = serverToolName;
        _logger = logger;

        Name = ToolNaming.Compose(serverName, serverToolName);
        if (!ToolNaming.IsValidWireName(Name))
        {
            throw new ArgumentException(
                $"Composed wire name '{Name}' for MCP tool '{serverName}.{serverToolName}' " +
                "is not valid (must match [a-zA-Z0-9_-]{1,64}). Rename the server or the tool.",
                nameof(serverToolName));
        }

        Definition = new ToolDefinition(Name, description, parameters);
    }

    public string Name { get; }
    public ToolDefinition Definition { get; }

    /// <summary>The original (un-prefixed) tool name as the MCP server advertises it.</summary>
    public string ServerToolName => _serverToolName;

    /// <summary>The MCP server this tool came from.</summary>
    public string ServerName => _serverName;

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = DateTimeOffset.UtcNow;
        try
        {
            var client = await _pool.GetAsync(_serverName, cancellationToken).ConfigureAwait(false);
            var args = ConvertArguments(request.Arguments);

            var result = await client.CallToolAsync(
                _serverToolName,
                args,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - started;
            return TranslateResult(request, result, elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP tool {Tool} threw", Name);
            return ToolResult.Failed(request.ToolCallId, request.ToolName, ex.Message,
                DateTimeOffset.UtcNow - started);
        }
    }

    private static Dictionary<string, object?> ConvertArguments(JsonObject arguments)
    {
        var dict = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            // Pass JsonNode directly — the SDK serializes whatever we hand it.
            // Strings, numbers, bools, nested objects/arrays all flow through as JsonValue/Object/Array.
            dict[key] = value;
        }
        return dict;
    }

    private static ToolResult TranslateResult(ToolRequest request, CallToolResult result, TimeSpan elapsed)
    {
        var summary = ConcatTextContent(result.Content);
        var data = new JsonObject
        {
            ["content"] = ContentToJsonArray(result.Content),
        };
        if (result.StructuredContent is { } structured)
        {
            data["structured"] = JsonNode.Parse(structured.GetRawText());
        }

        var success = result.IsError != true;
        return new ToolResult(
            ToolCallId: request.ToolCallId,
            ToolName: request.ToolName,
            Success: success,
            Data: data,
            Summary: success ? summary : "MCP tool reported an error",
            ResultItemCount: result.Content?.Count ?? 0,
            Duration: elapsed,
            Error: success ? null : summary);
    }

    private static string ConcatTextContent(IList<ContentBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            if (b is TextContentBlock t && !string.IsNullOrEmpty(t.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.Text);
            }
        }
        return sb.ToString();
    }

    private static JsonArray ContentToJsonArray(IList<ContentBlock>? blocks)
    {
        var arr = new JsonArray();
        if (blocks is null) return arr;
        foreach (var b in blocks)
        {
            arr.Add(new JsonObject
            {
                ["type"] = b.Type,
                ["text"] = b is TextContentBlock t ? t.Text : null,
            });
        }
        return arr;
    }
}
