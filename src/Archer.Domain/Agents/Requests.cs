namespace Archer.Domain.Agents;

public sealed record NewAgentRequest(
    string RepoRoot,
    string FirstUserPrompt,
    string AgentDefinitionId = "code-scout",
    string? ModelDeployment = null,
    int? MaxCompletionTokens = null);

public sealed record UserMessageInput(string Content);

public sealed record UserMessageAccepted(
    long MessageSeq,
    Guid NewTurnId,
    Guid? SupersededTurnId);

public sealed record InterruptRequest(string Reason);

public sealed record AssistantMessage(string Text);

public sealed record TurnRunRequest(
    string AgentId,
    Guid TurnId,
    long StartedAtMessageSeq);
