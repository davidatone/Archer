namespace Archer.Domain.Agents;

public sealed record AgentSnapshot(
    string AgentId,
    string RepoRoot,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long LatestMessageSeq,
    Guid? ActiveTurnId,
    long? ActiveTurnStartedAtSeq,
    string AgentDefinitionId,
    string? ModelDeployment,
    int? MaxCompletionTokens,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<Summary> Summaries)
{
    public static AgentSnapshot From(AgentState state) => new(
        state.AgentId,
        state.RepoRoot,
        state.CreatedAtUtc,
        state.UpdatedAtUtc,
        state.LatestMessageSeq,
        state.ActiveTurnId,
        state.ActiveTurnStartedAtSeq,
        state.AgentDefinitionId,
        state.ModelDeployment,
        state.MaxCompletionTokens,
        state.Messages.AsReadOnly(),
        state.Summaries.AsReadOnly());
}
