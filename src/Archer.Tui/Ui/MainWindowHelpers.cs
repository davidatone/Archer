namespace Archer.Tui.Ui;

/// <summary>
/// Pure helpers used by <see cref="MainWindow"/>. Extracted so the dialog/menu logic can be
/// unit-tested without booting Terminal.Gui — the window itself becomes a thin wrapper that
/// pipes view state into these functions.
/// </summary>
internal static class MainWindowHelpers
{
    /// <summary>
    /// Pick the agent type to use when none was specified. Alphabetically-first registered
    /// definition; falls back to <c>code-scout</c> if no definitions are registered yet so the
    /// startup tab still works in a fresh repo without YAML.
    /// </summary>
    public static string PickDefaultAgentType(IEnumerable<string> registeredIds)
    {
        return registeredIds
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .FirstOrDefault() ?? "code-scout";
    }

    /// <summary>
    /// Format the short tab label used by <see cref="MainWindow"/>'s TabView. Agent ids look like
    /// <c>agent_TESTAGENTXXX1</c>; the tab shows just the random middle slice so the strip stays
    /// scannable when many agents are open.
    /// </summary>
    public static string FormatTabLabel(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        if (agentId.Length < 12) return $" {agentId} ";
        return $" {agentId[6..12]} ";
    }

    /// <summary>
    /// Validate raw user-entered text from the "New agent" dialog's repo input. Returns the
    /// canonical full path on success; sets <paramref name="error"/> with a user-facing message
    /// otherwise. Pure: doesn't open dialogs or touch UI state.
    /// </summary>
    public static bool TryResolveRepoPath(string? raw, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Path is required.";
            return false;
        }
        resolved = Path.GetFullPath(trimmed);
        if (!Directory.Exists(resolved))
        {
            error = $"Not a directory: {resolved}";
            return false;
        }
        return true;
    }
}
