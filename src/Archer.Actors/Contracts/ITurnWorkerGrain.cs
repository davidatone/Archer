using Archer.Domain.Agents;
using Orleans;

namespace Archer.Actors.Contracts;

[Alias("Archer.ITurnWorkerGrain")]
public interface ITurnWorkerGrain : IGrainWithGuidKey
{
    [Alias("RunTurn")] Task RunTurnAsync(TurnRunRequest request);
    [Alias("Cancel")] Task CancelAsync();
}
