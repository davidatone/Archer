using System.Text.Json.Nodes;

namespace Archer.Domain.Model;

public sealed record ModelToolCall(
    string ToolCallId,
    string ToolName,
    JsonObject Arguments);
