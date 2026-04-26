using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Archer.Application.Telemetry;

/// <summary>
/// Central <see cref="ActivitySource"/> and <see cref="Meter"/> for Archer. Anything that wants
/// to participate in tracing or metrics should use these so a single OTel registration in the
/// host wires up everything (turns, tool calls, model calls, summarisation, etc.).
/// </summary>
public static class ArcherTelemetry
{
    public const string SourceName = "Archer";
    public const string MeterName = "Archer";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Counter — number of agent turns started.</summary>
    public static readonly Counter<long> TurnsStarted =
        Meter.CreateCounter<long>("archer.turns.started", description: "Agent turns started.");

    /// <summary>Counter — number of agent turns completed (final answer committed).</summary>
    public static readonly Counter<long> TurnsCompleted =
        Meter.CreateCounter<long>("archer.turns.completed", description: "Agent turns completed.");

    /// <summary>Counter — number of agent turns superseded.</summary>
    public static readonly Counter<long> TurnsSuperseded =
        Meter.CreateCounter<long>("archer.turns.superseded", description: "Agent turns superseded by newer messages.");

    /// <summary>Counter — number of agent turns that failed with an error.</summary>
    public static readonly Counter<long> TurnsFailed =
        Meter.CreateCounter<long>("archer.turns.failed", description: "Agent turns that exited with an error.");

    /// <summary>Counter — number of tool calls executed.</summary>
    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("archer.tool.calls", description: "Tool invocations issued by an agent.");

    /// <summary>Histogram — duration of tool executions in milliseconds.</summary>
    public static readonly Histogram<double> ToolDurationMs =
        Meter.CreateHistogram<double>("archer.tool.duration", unit: "ms", description: "Tool execution duration.");

    /// <summary>Histogram — duration of model calls (one model.GetResponseAsync) in milliseconds.</summary>
    public static readonly Histogram<double> ModelCallDurationMs =
        Meter.CreateHistogram<double>("archer.model.call.duration", unit: "ms", description: "Model call duration.");

    /// <summary>Counter — number of model-level errors.</summary>
    public static readonly Counter<long> ModelErrors =
        Meter.CreateCounter<long>("archer.model.errors", description: "Model invocations that returned an error.");

    /// <summary>Standard tag keys used across Archer activities.</summary>
    public static class Tags
    {
        public const string AgentId = "archer.agent.id";
        public const string AgentDefinition = "archer.agent.definition";
        public const string TurnId = "archer.turn.id";
        public const string ToolName = "archer.tool.name";
        public const string ToolCallId = "archer.tool.call.id";
        public const string Deployment = "archer.model.deployment";
        public const string FinishReason = "archer.model.finish_reason";
    }
}
