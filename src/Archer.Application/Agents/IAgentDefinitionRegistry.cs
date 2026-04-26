using Archer.Domain.Agents;

namespace Archer.Application.Agents;

public interface IAgentDefinitionRegistry
{
    /// <summary>Resolve an agent definition by id, or null if not registered.</summary>
    AgentDefinition? Get(string id);

    /// <summary>All registered definitions.</summary>
    IReadOnlyList<AgentDefinition> All { get; }
}
