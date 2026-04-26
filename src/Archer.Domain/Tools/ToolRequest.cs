using System.Text.Json.Nodes;

namespace Archer.Domain.Tools;

public sealed record ToolRequest(
    string ToolCallId,
    string ToolName,
    JsonObject Arguments,
    string RepoRoot,
    string AgentId);
