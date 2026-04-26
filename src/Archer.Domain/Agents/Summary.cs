namespace Archer.Domain.Agents;

public sealed record Summary
{
    public required Guid TurnId { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
