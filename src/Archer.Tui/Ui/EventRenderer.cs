using Archer.Domain.Events;

namespace Archer.Tui.Ui;

/// <summary>
/// Pure mapping from an <see cref="AgentEvent"/> to a structured render instruction. Pulled
/// out of <see cref="AgentTabView"/> so the format/branching logic can be unit-tested without
/// booting Terminal.Gui — the view code is then a thin "apply the instruction" wrapper.
/// </summary>
internal static class EventRenderer
{
    public enum ReasoningOp { None, Clear, Set }

    /// <summary>What the view should do after this event arrives. Empty fields = no-op.</summary>
    public sealed record RenderInstruction
    {
        public string? AppendToChat { get; init; }
        public string? AppendToEvents { get; init; }
        public ReasoningOp Reasoning { get; init; } = ReasoningOp.None;
        /// <summary>Only meaningful when <see cref="Reasoning"/> is <c>Set</c>.</summary>
        public string? SetReasoning { get; init; }
    }

    public static RenderInstruction Render(AgentEvent evt) => evt switch
    {
        TurnStartedEvent ts => new RenderInstruction
        {
            AppendToEvents = $"⏵ turn {ts.MessageSeq:0000}",
            Reasoning = ReasoningOp.Clear,
        },
        ModelStartedEvent ms => new RenderInstruction
        {
            AppendToEvents = $"✦ {ms.Model}",
        },
        ToolCallStartedEvent tcs => new RenderInstruction
        {
            // Keep reasoning visible — it explains why this tool is being called. It'll be
            // replaced when the model emits new reasoning before the next iteration, or
            // cleared on final-answer / turn-end.
            AppendToEvents = $"🔧 {tcs.ToolName} {Compact(tcs.Arguments.ToJsonString())}",
        },
        ToolCallCompletedEvent { Success: true } tcc => new RenderInstruction
        {
            AppendToEvents = $"   ↳ {tcc.Summary}",
        },
        ToolCallCompletedEvent tcc => new RenderInstruction
        {
            AppendToEvents = $"   ↳ ⚠ {tcc.Error}",
        },
        ReasoningEvent r when !string.IsNullOrWhiteSpace(r.Text) => new RenderInstruction
        {
            Reasoning = ReasoningOp.Set,
            SetReasoning = r.Text,
        },
        SummaryEvent s when !string.IsNullOrWhiteSpace(s.Message) => new RenderInstruction
        {
            AppendToEvents = $"≡ {s.Message}",
        },
        TurnSupersededEvent tss => new RenderInstruction
        {
            AppendToEvents = $"⏹ superseded — {tss.Reason}",
            Reasoning = ReasoningOp.Clear,
        },
        FinalAnswerEvent fa => new RenderInstruction
        {
            AppendToChat = $"\nagent> {fa.Text}\n",
            Reasoning = ReasoningOp.Clear,
        },
        TurnCompletedEvent => new RenderInstruction
        {
            AppendToEvents = "⏹ turn complete",
            Reasoning = ReasoningOp.Clear,
        },
        TurnFailedEvent tf => new RenderInstruction
        {
            AppendToChat = $"\n[error] {tf.Error}\n",
            AppendToEvents = $"⚠ turn failed: {tf.Error}",
            Reasoning = ReasoningOp.Clear,
        },
        _ => new RenderInstruction(),
    };

    public static string Compact(string s) => s.Length > 90 ? s[..87] + "..." : s;
}
