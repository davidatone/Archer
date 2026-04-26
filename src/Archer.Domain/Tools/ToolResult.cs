using System.Text.Json.Nodes;

namespace Archer.Domain.Tools;

public sealed record ToolResult(
    string ToolCallId,
    string ToolName,
    bool Success,
    JsonObject Data,
    string Summary,
    int ResultItemCount = 0,
    TimeSpan Duration = default,
    string? Error = null)
{
    public static ToolResult Failed(string toolCallId, string toolName, string error, TimeSpan duration = default) =>
        new(toolCallId, toolName, false, [], error, 0, duration, error);
}
