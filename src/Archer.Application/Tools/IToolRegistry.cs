using Archer.Domain.Tools;

namespace Archer.Application.Tools;

/// <summary>
/// Live, thread-safe registry of tools. The <see cref="Tools"/> and <see cref="Definitions"/>
/// properties return the current snapshot — readers always see a coherent list.
/// Tools may be added or removed at runtime (e.g. when an MCP server connects), in which
/// case <see cref="Changed"/> fires after the snapshot has been updated.
/// </summary>
public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Look up a tool by name. Accepts either logical (<c>trello.list_boards</c>)
    /// or wire (<c>trello__list_boards</c>) form.</summary>
    bool TryGet(string name, out ITool tool);

    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Register a tool. The tool's <see cref="ITool.Name"/> must be a valid wire
    /// name. Replaces any existing tool with the same name.</summary>
    void Add(ITool tool);

    /// <summary>Remove a tool by name (logical or wire form). Returns true if removed.</summary>
    bool Remove(string name);

    /// <summary>Fired (after the snapshot has been rebuilt) when a tool is added or removed.</summary>
    event Action<ToolRegistryChange>? Changed;
}

public sealed record ToolRegistryChange(ToolRegistryChangeKind Kind, ITool Tool);

public enum ToolRegistryChangeKind
{
    Added,
    Removed,
}
