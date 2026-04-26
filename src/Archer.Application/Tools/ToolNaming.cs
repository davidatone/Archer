namespace Archer.Application.Tools;

/// <summary>
/// Translates between human-readable logical tool names (<c>trello.list_boards</c>)
/// and wire names compatible with Azure OpenAI / OpenAI's function-name regex
/// <c>^[a-zA-Z0-9_-]{1,64}$</c> (<c>trello__list_boards</c>). Built-in tools
/// without a server prefix pass through unchanged in both directions.
/// </summary>
public static class ToolNaming
{
    /// <summary>Wire-form separator between server name and tool name.</summary>
    public const string WireSeparator = "__";

    /// <summary>Logical-form separator between server name and tool name.</summary>
    public const char LogicalSeparator = '.';

    /// <summary>Convert a logical name (with dot) to a wire name (with double underscore).
    /// Names without a separator pass through unchanged.</summary>
    public static string ToWire(string logical)
    {
        ArgumentNullException.ThrowIfNull(logical);
        var dot = logical.IndexOf(LogicalSeparator);
        return dot < 0 ? logical : logical[..dot] + WireSeparator + logical[(dot + 1)..];
    }

    /// <summary>Convert a wire name (with double underscore) to a logical name (with dot).
    /// Names without a separator, or names whose first separator isn't <c>__</c>, pass through.</summary>
    public static string ToLogical(string wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var idx = wire.IndexOf(WireSeparator, StringComparison.Ordinal);
        return idx <= 0 ? wire : wire[..idx] + LogicalSeparator + wire[(idx + WireSeparator.Length)..];
    }

    /// <summary>True iff the input matches OpenAI/Azure's function-name regex.</summary>
    public static bool IsValidWireName(string wire)
    {
        if (string.IsNullOrEmpty(wire) || wire.Length > 64)
        {
            return false;
        }
        for (var i = 0; i < wire.Length; i++)
        {
            var c = wire[i];
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '_' || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compose a wire name from a server name and a server-local tool name.</summary>
    public static string Compose(string serverName, string toolName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(toolName);
        return serverName + WireSeparator + toolName;
    }

    /// <summary>Extract the server prefix from a name in either logical or wire form.
    /// Returns null if the name has no server prefix (built-in tool).</summary>
    public static string? ServerPrefix(string nameInEitherForm)
    {
        ArgumentNullException.ThrowIfNull(nameInEitherForm);
        var dotIdx = nameInEitherForm.IndexOf(LogicalSeparator);
        if (dotIdx > 0)
        {
            return nameInEitherForm[..dotIdx];
        }
        var underIdx = nameInEitherForm.IndexOf(WireSeparator, StringComparison.Ordinal);
        return underIdx > 0 ? nameInEitherForm[..underIdx] : null;
    }
}
