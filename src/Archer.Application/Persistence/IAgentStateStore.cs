using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;

namespace Archer.Application.Persistence;

public interface IAgentStateStore
{
    Task<AgentState?> LoadAsync(string agentId, CancellationToken cancellationToken = default);
    Task SaveAsync(AgentState state, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string agentId, CancellationToken cancellationToken = default);
    Task AppendEventAsync(string agentId, AgentEvent evt, CancellationToken cancellationToken = default);
    Task SaveToolResultAsync(string agentId, Guid turnId, int index, ToolResult result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAgentsAsync(CancellationToken cancellationToken = default);
}
