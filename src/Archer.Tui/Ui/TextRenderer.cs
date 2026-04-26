using Archer.Domain.Agents;

namespace Archer.Tui.Ui;

/// <summary>
/// Pure formatters used by <see cref="AgentTabView"/>. Extracted so the layout logic can
/// be unit-tested without booting Terminal.Gui.
/// </summary>
internal static class TextRenderer
{
    /// <summary>Short user-visible label for a message role: "you", "agent", or the lowercased role name.</summary>
    public static string LabelForRole(MessageRole role) => role switch
    {
        MessageRole.User => "you",
        MessageRole.Assistant => "agent",
        _ => role.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Compose the chat pane's body text from the committed transcript and any live
    /// reasoning block. Inserts a separator newline if the committed transcript doesn't
    /// already end in one, so the reasoning marker doesn't run onto the prior line.
    /// </summary>
    public static string ComposeChat(string committed, string liveReasoning)
    {
        if (string.IsNullOrEmpty(liveReasoning))
        {
            return committed;
        }
        var needsNewline = committed.Length > 0 && committed[^1] != '\n';
        var capacity = committed.Length + liveReasoning.Length + (needsNewline ? Environment.NewLine.Length : 0);
        var sb = new System.Text.StringBuilder(capacity);
        sb.Append(committed);
        if (needsNewline) sb.AppendLine();
        sb.Append(liveReasoning);
        return sb.ToString();
    }

    /// <summary>Format the committed transcript from the session's message list.</summary>
    public static string FormatTranscript(IEnumerable<AgentMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in messages)
        {
            sb.AppendLine($"{LabelForRole(m.Role)}> {m.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Format one task-list entry as the glyph + title display string used by the side pane.</summary>
    public static string FormatTodo(TodoItem todo)
    {
        var glyph = todo.Status switch
        {
            TodoStatus.Done => "✓",
            TodoStatus.Doing => "◐",
            TodoStatus.Blocked => "✗",
            _ => "☐",
        };
        return $"{glyph} {todo.Title}";
    }

    /// <summary>
    /// Reasoning arrives as <c>**Header**\n\nbody...</c>. Render the header on its own line with
    /// a marker glyph and the body indented underneath, so it stands out against committed
    /// you/agent turns.
    /// </summary>
    public static string FormatReasoning(string text)
    {
        var trimmed = text.Trim();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("┄ thinking ┄");

        // Pull out leading **header** if present.
        if (trimmed.StartsWith("**", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf("**", 2, StringComparison.Ordinal);
            if (end > 2)
            {
                var header = trimmed[2..end].Trim();
                var body = trimmed[(end + 2)..].Trim();
                sb.Append("▸ ").AppendLine(header);
                if (body.Length > 0)
                {
                    foreach (var line in body.Split('\n'))
                    {
                        sb.Append("  ").AppendLine(line.TrimEnd());
                    }
                }
                return sb.ToString();
            }
        }

        // No bold header — render the whole block under a generic marker.
        foreach (var line in trimmed.Split('\n'))
        {
            sb.Append("  ").AppendLine(line.TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>Format the bottom status bar.</summary>
    public static string FormatStatus(string agentId, Guid? activeTurnId, bool isThinking, int messageCount, int todoCount)
    {
        var turn = activeTurnId is { } id ? id.ToString("N")[..6] : "idle";
        var thinking = isThinking ? " thinking…" : "";
        return $" {agentId}  •  turn:{turn}{thinking}  •  msgs:{messageCount}  •  todos:{todoCount}";
    }
}
