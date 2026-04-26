using Archer.Application.Model;
using Archer.Application.Tools;
using Archer.Domain.Agents;
using Archer.Domain.Model;

namespace Archer.Model.AgentFramework;

/// <summary>
/// Builds a <see cref="ModelTurnInput"/> from agent state + the agent's <see cref="AgentDefinition"/>.
/// Honors the definition's instructions, tool whitelist, model profile, and percentage-based
/// recent-message window. Falls back to sensible defaults when fields are omitted.
/// </summary>
public sealed class AgentContextBuilder : IAgentContextBuilder
{
    /// <summary>Approximate token-per-character ratio for fitting messages to a token budget.</summary>
    private const int CharsPerToken = 4;

    /// <summary>Default token budget when the model definition doesn't specify a context window.</summary>
    private const int DefaultContextWindowTokens = 128_000;

    private readonly IToolRegistry _tools;

    public AgentContextBuilder(IToolRegistry tools)
    {
        _tools = tools;
    }

    public ModelTurnInput Build(AgentState state, Guid turnId, AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(definition);

        var recent = SelectRecentMessages(state.Messages, definition).ToArray();
        var instructions = BuildInstructions(state, definition);
        var tools = FilterToolsByDefinition(definition.Tools);

        return new ModelTurnInput(
            AgentId: state.AgentId,
            TurnId: turnId,
            SystemInstructions: instructions,
            Messages: recent,
            Tools: tools,
            ModelDeployment: state.ModelDeployment ?? definition.Model.Deployment,
            MaxCompletionTokens: state.MaxCompletionTokens ?? definition.Model.MaxCompletionTokens,
            ReasoningEffort: definition.Model.Reasoning?.Effort,
            ReasoningSummary: definition.Model.Reasoning?.Summary);
    }

    private static string BuildInstructions(AgentState state, AgentDefinition definition)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(definition.Instructions.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Repository root: ").AppendLine(state.RepoRoot);

        if (state.Summaries.Count > 0)
        {
            sb.AppendLine();
            sb.Append("Latest summary: ").AppendLine(state.Summaries[^1].Content);
        }

        return sb.ToString();
    }

    private static List<AgentMessage> SelectRecentMessages(
        List<AgentMessage> all,
        AgentDefinition definition)
    {
        if (all.Count == 0)
        {
            return [];
        }

        var ordered = all.OrderBy(m => m.Seq).ToList();
        var contextTokens = definition.Model.ContextWindowTokens ?? DefaultContextWindowTokens;
        var budgetTokens = Math.Max(1, definition.Context.RecentMessageWindow.RoundUp(contextTokens));

        // Walk newest-first, accumulate until we exhaust the budget.
        var picked = new LinkedList<AgentMessage>();
        var used = 0;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var msg = ordered[i];
            var cost = ApproximateTokens(msg);
            if (used + cost > budgetTokens && picked.Count > 0)
            {
                break;
            }
            picked.AddFirst(msg);
            used += cost;
        }

        // Always pin the first message if requested and not already included.
        if (definition.Context.PinFirstMessage && picked.First?.Value.Seq != ordered[0].Seq)
        {
            picked.AddFirst(ordered[0]);
        }

        return [.. picked];
    }

    private static int ApproximateTokens(AgentMessage m) =>
        Math.Max(1, m.Content.Length / CharsPerToken);

    /// <summary>
    /// Resolve a per-agent tool whitelist into the live set of <see cref="Domain.Tools.ToolDefinition"/>s
    /// the model should see this turn. Semantics:
    /// <list type="bullet">
    ///   <item><c>[]</c> (empty) → no tools.</item>
    ///   <item><c>["*"]</c> or any list containing <c>"*"</c> → every tool currently registered.</item>
    ///   <item><c>"&lt;server&gt;.*"</c> or <c>"&lt;server&gt;__*"</c> → every tool whose wire name has that prefix.</item>
    ///   <item>An exact name in either logical or wire form → match the corresponding tool.</item>
    /// </list>
    /// Resolution happens at turn-start time, so newly-registered MCP tools become available
    /// for an agent without restart as long as the agent's whitelist already covers them.
    /// </summary>
    private IReadOnlyList<Domain.Tools.ToolDefinition> FilterToolsByDefinition(IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0)
        {
            return [];
        }

        var wildcardServers = new HashSet<string>(StringComparer.Ordinal);
        var explicitWireNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in allowed)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            if (entry == "*")
            {
                return _tools.Definitions;
            }
            if (entry.EndsWith(".*", StringComparison.Ordinal))
            {
                wildcardServers.Add(entry[..^2]);
            }
            else if (entry.EndsWith("__*", StringComparison.Ordinal))
            {
                wildcardServers.Add(entry[..^3]);
            }
            else
            {
                explicitWireNames.Add(ToolNaming.ToWire(entry));
            }
        }

        return [.. _tools.Definitions.Where(d =>
        {
            if (explicitWireNames.Contains(d.Name))
            {
                return true;
            }
            var prefix = ToolNaming.ServerPrefix(d.Name);
            return prefix is not null && wildcardServers.Contains(prefix);
        })];
    }
}
