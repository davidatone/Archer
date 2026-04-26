using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;
using Orleans;

namespace Archer.Actors.Contracts;

[Alias("Archer.IArcherAgentGrain")]
public interface IArcherAgentGrain : IGrainWithStringKey
{
    [Alias("Initialize")] Task<AgentSnapshot> InitializeAsync(NewAgentRequest request);
    [Alias("AddUserMessage")] Task<UserMessageAccepted> AddUserMessageAsync(UserMessageInput input);
    [Alias("Interrupt")] Task InterruptAsync(InterruptRequest request);
    /// <summary>Returns null when the agent has never been initialized.</summary>
    [Alias("GetSnapshot")] Task<AgentSnapshot?> GetSnapshotAsync();
    [Alias("IsTurnStillActive")] Task<bool> IsTurnStillActiveAsync(Guid turnId, long messageSeq);
    [Alias("AppendTurnEvent")] Task AppendTurnEventAsync(Guid turnId, AgentEvent evt);
    [Alias("RecordToolResult")] Task RecordToolResultAsync(Guid turnId, int index, ToolResult result);
    [Alias("CommitFinalAnswerIfStillActive")] Task<bool> CommitFinalAnswerIfStillActiveAsync(Guid turnId, long messageSeq, AssistantMessage final);
}
