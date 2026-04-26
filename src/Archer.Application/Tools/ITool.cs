using Archer.Domain.Tools;

namespace Archer.Application.Tools;

public interface ITool
{
    string Name { get; }
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
