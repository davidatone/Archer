using System.Text.Json.Nodes;

namespace Archer.Domain.Events;

public abstract record AgentEvent
{
    public required string AgentId { get; init; }
    public required Guid TurnId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public abstract string Kind { get; }
}

public sealed record TurnStartedEvent : AgentEvent
{
    public override string Kind => "turn_started";
    public required long MessageSeq { get; init; }
}

public sealed record ModelStartedEvent : AgentEvent
{
    public override string Kind => "model_started";
    public required string Model { get; init; }
}

public sealed record ToolCallStartedEvent : AgentEvent
{
    public override string Kind => "tool_call_started";
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required JsonObject Arguments { get; init; }
}

public sealed record ToolCallCompletedEvent : AgentEvent
{
    public override string Kind => "tool_call_completed";
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int ResultItemCount { get; init; }
    public required string Summary { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed record SummaryEvent : AgentEvent
{
    public override string Kind => "summary";
    public required string Message { get; init; }
}

public sealed record ReasoningEvent : AgentEvent
{
    public override string Kind => "reasoning";
    public required string Text { get; init; }
}

public sealed record FinalAnswerEvent : AgentEvent
{
    public override string Kind => "final_answer";
    public required string Text { get; init; }
}

public sealed record TurnSupersededEvent : AgentEvent
{
    public override string Kind => "turn_superseded";
    public required string Reason { get; init; }
}

public sealed record TurnCompletedEvent : AgentEvent
{
    public override string Kind => "turn_completed";
}

public sealed record TurnFailedEvent : AgentEvent
{
    public override string Kind => "turn_failed";
    public required string Error { get; init; }
}
