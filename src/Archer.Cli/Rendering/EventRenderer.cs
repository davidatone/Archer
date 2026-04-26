using System.Text.Json;
using Archer.Domain.Events;

namespace Archer.Cli.Rendering;

internal static class EventRenderer
{
    public static void Render(AgentEvent evt, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(writer);

        switch (evt)
        {
            case TurnStartedEvent ts:
                writer.WriteLine(Ansi.Wrap($"[turn:{ts.MessageSeq:0000}] started", Ansi.BrightCyan));
                break;
            case ModelStartedEvent ms:
                writer.WriteLine(Ansi.Wrap($"[model] thinking ({ms.Model})", Ansi.Yellow));
                break;
            case ToolCallStartedEvent tcs:
                writer.WriteLine(Ansi.Wrap($"[tool] {tcs.ToolName} {Compact(tcs.Arguments)}", Ansi.Magenta));
                break;
            case ToolCallCompletedEvent tcc:
                if (tcc.Success)
                {
                    writer.WriteLine(Ansi.Wrap($"[tool] {tcc.ToolName} completed: {tcc.Summary}", Ansi.Green));
                }
                else
                {
                    writer.WriteLine(Ansi.Wrap($"[tool] {tcc.ToolName} failed: {tcc.Error ?? "unknown error"}", Ansi.BrightRed));
                }
                break;
            case SummaryEvent se:
                writer.WriteLine(Ansi.Wrap($"[summary] {se.Message}", Ansi.Blue));
                break;
            case ReasoningEvent re:
                foreach (var line in re.Text.Split('\n'))
                {
                    writer.WriteLine(Ansi.Wrap($"[reasoning] {line}", Ansi.Gray + Ansi.Italic));
                }
                break;
            case TurnSupersededEvent tss:
                writer.WriteLine(Ansi.Wrap($"[superseded] {tss.Reason}", Ansi.BrightYellow));
                break;
            case FinalAnswerEvent fa:
                writer.WriteLine();
                writer.WriteLine(Ansi.Wrap("Final:", Ansi.Bold + Ansi.BrightGreen));
                writer.WriteLine(Ansi.Wrap(fa.Text, Ansi.BrightWhite));
                break;
            case TurnCompletedEvent:
                writer.WriteLine(Ansi.Wrap("[turn] complete", Ansi.Green));
                break;
            case TurnFailedEvent tf:
                writer.WriteLine(Ansi.Wrap($"[turn] failed: {tf.Error}", Ansi.BrightRed));
                break;
            default:
                writer.WriteLine(Ansi.Wrap($"[{evt.Kind}]", Ansi.Dim));
                break;
        }
    }

    private static string Compact(System.Text.Json.Nodes.JsonObject obj)
    {
        var s = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return s.Length > 120 ? s[..117] + "..." : s;
    }
}
