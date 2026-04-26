using Archer.Domain.Agents;
using Archer.Domain.Model;

namespace Archer.Application.Model;

/// <summary>Builds a model-turn input from agent state + the agent's loaded definition.</summary>
public interface IAgentContextBuilder
{
    ModelTurnInput Build(AgentState state, Guid turnId, AgentDefinition definition);
}
