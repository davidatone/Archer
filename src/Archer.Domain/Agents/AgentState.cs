namespace Archer.Domain.Agents;

public sealed class AgentState
{
    public required string AgentId { get; init; }
    public required string RepoRoot { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long LatestMessageSeq { get; set; }
    public Guid? ActiveTurnId { get; set; }
    public long? ActiveTurnStartedAtSeq { get; set; }
    public required string AgentDefinitionId { get; set; }
    public string? ModelDeployment { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public List<AgentMessage> Messages { get; init; } = [];
    public List<Summary> Summaries { get; init; } = [];

    public bool IsTurnActive(Guid turnId, long messageSeq) =>
        ActiveTurnId == turnId && ActiveTurnStartedAtSeq == messageSeq;
}
