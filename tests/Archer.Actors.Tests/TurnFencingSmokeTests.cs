using Archer.Domain.Agents;
using FluentAssertions;

namespace Archer.Actors.Tests;

/// <summary>
/// Smoke-level tests for the latest-turn-wins rule in <see cref="AgentState"/> directly.
/// Full grain-level fencing is exercised by the integration tests (TBD with TestCluster).
/// </summary>
public class TurnFencingSmokeTests
{
    [Fact]
    public void Active_turn_is_recognized()
    {
        var state = NewState();
        var turn = Guid.NewGuid();
        state.ActiveTurnId = turn;
        state.ActiveTurnStartedAtSeq = 1;
        state.LatestMessageSeq = 1;

        state.IsTurnActive(turn, 1).Should().BeTrue();
    }

    [Fact]
    public void Stale_turn_is_rejected_after_supersede()
    {
        var state = NewState();
        var turnA = Guid.NewGuid();
        state.ActiveTurnId = turnA;
        state.ActiveTurnStartedAtSeq = 1;

        // Supersede with B at seq 2
        var turnB = Guid.NewGuid();
        state.ActiveTurnId = turnB;
        state.ActiveTurnStartedAtSeq = 2;

        state.IsTurnActive(turnA, 1).Should().BeFalse();
        state.IsTurnActive(turnB, 2).Should().BeTrue();
    }

    private static AgentState NewState() => new()
    {
        AgentId = AgentId.New(),
        AgentDefinitionId = "code-scout",
        RepoRoot = "/tmp",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        LatestMessageSeq = 0,
    };
}
