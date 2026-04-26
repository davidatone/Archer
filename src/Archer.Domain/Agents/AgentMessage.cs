namespace Archer.Domain.Agents;

public sealed record AgentMessage
{
    public required long Seq { get; init; }
    public required MessageRole Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
}
