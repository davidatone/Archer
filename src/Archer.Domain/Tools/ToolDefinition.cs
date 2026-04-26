using System.Text.Json.Nodes;

namespace Archer.Domain.Tools;

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject Parameters);
