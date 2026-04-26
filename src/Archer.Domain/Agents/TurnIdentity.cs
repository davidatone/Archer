namespace Archer.Domain.Agents;

public sealed record TurnIdentity(Guid TurnId, long StartedAtMessageSeq);
