using Archer.Domain.Model;

namespace Archer.Application.Model;

public interface IModelTurnRunner
{
    IAsyncEnumerable<IModelTurnUpdate> RunAsync(
        ModelTurnInput input,
        IReadOnlyList<ModelToolResultEntry> priorToolResults,
        CancellationToken cancellationToken);
}

public sealed record ModelToolResultEntry(
    string ToolCallId,
    string ToolName,
    System.Text.Json.Nodes.JsonObject Arguments,
    string ResultJson);
