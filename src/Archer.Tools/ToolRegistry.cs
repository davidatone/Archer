using Archer.Application.Tools;
using Archer.Domain.Tools;
using Microsoft.Extensions.Logging;

namespace Archer.Tools;

/// <summary>
/// Thread-safe, mutable tool registry. Stores tools by their wire name (the form sent to
/// the model). Lookups accept either logical (<c>trello.list_boards</c>) or wire
/// (<c>trello__list_boards</c>) form — both normalize to the wire key on the way in.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ILogger<ToolRegistry> _logger;

    private IReadOnlyList<ITool> _tools = [];
    private IReadOnlyList<ToolDefinition> _definitions = [];

    public event Action<ToolRegistryChange>? Changed;

    public ToolRegistry(IEnumerable<ITool> tools, ILogger<ToolRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        foreach (var tool in tools)
        {
            ValidateWireName(tool);
            _byName[tool.Name] = tool;
        }
        RebuildSnapshot();
    }

    public IReadOnlyList<ITool> Tools => _tools;
    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    public bool TryGet(string name, out ITool tool)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wire = ToolNaming.ToWire(name);
        if (_byName.TryGetValue(wire, out var found))
        {
            tool = found;
            return true;
        }
        tool = null!;
        return false;
    }

    public void Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ValidateWireName(tool);

        lock (_gate)
        {
            _byName[tool.Name] = tool;
            RebuildSnapshot();
        }
        Changed?.Invoke(new ToolRegistryChange(ToolRegistryChangeKind.Added, tool));
    }

    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wire = ToolNaming.ToWire(name);

        ITool? removed = null;
        lock (_gate)
        {
            if (_byName.TryGetValue(wire, out var existing))
            {
                _byName.Remove(wire);
                removed = existing;
                RebuildSnapshot();
            }
        }
        if (removed is null)
        {
            return false;
        }
        Changed?.Invoke(new ToolRegistryChange(ToolRegistryChangeKind.Removed, removed));
        return true;
    }

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wire = ToolNaming.ToWire(request.ToolName);
        if (!_byName.TryGetValue(wire, out var tool))
        {
            _logger.LogWarning("Unknown tool {Tool}", request.ToolName);
            return ToolResult.Failed(request.ToolCallId, request.ToolName, $"Unknown tool: {request.ToolName}");
        }

        try
        {
            return await tool.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} threw", request.ToolName);
            return ToolResult.Failed(request.ToolCallId, request.ToolName, ex.Message);
        }
    }

    private void RebuildSnapshot()
    {
        var snap = _byName.Values.ToList();
        _tools = snap;
        _definitions = [.. snap.Select(t => t.Definition)];
    }

    private static void ValidateWireName(ITool tool)
    {
        if (!ToolNaming.IsValidWireName(tool.Name))
        {
            throw new ArgumentException(
                $"Tool name '{tool.Name}' is not a valid wire name (must match [a-zA-Z0-9_-]{{1,64}}). " +
                "Use ToolNaming.Compose(server, name) or ToolNaming.ToWire(logical) when registering MCP tools.",
                nameof(tool));
        }
    }
}
