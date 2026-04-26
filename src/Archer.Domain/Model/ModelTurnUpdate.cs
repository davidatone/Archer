namespace Archer.Domain.Model;

/// <summary>Marker for the discriminated set of update events emitted by the model runner.</summary>
public interface IModelTurnUpdate { }

public sealed record ModelStartedUpdate(string Model) : IModelTurnUpdate;

public sealed record ModelTextDeltaUpdate(string Delta) : IModelTurnUpdate;

public sealed record ModelReasoningUpdate(string Text) : IModelTurnUpdate;

public sealed record ModelToolCallUpdate(ModelToolCall ToolCall) : IModelTurnUpdate;

public sealed record ModelFinalAnswerUpdate(string Text) : IModelTurnUpdate;

public sealed record ModelErrorUpdate(string Error) : IModelTurnUpdate;
