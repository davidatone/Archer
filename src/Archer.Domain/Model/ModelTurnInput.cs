using Archer.Domain.Agents;
using Archer.Domain.Tools;

namespace Archer.Domain.Model;

public sealed record ModelTurnInput(
    string AgentId,
    Guid TurnId,
    string SystemInstructions,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    string ModelDeployment,
    int? MaxCompletionTokens = null,
    ReasoningEffort? ReasoningEffort = null,
    ReasoningSummary? ReasoningSummary = null);
