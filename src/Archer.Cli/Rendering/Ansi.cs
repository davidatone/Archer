namespace Archer.Cli.Rendering;

/// <summary>
/// Minimal ANSI color helpers. Respects the NO_COLOR convention
/// (https://no-color.org) and disables when stdout is redirected.
/// </summary>
internal static class Ansi
{
    public static bool Enabled { get; } = ResolveEnabled();

    private const string Esc = "\u001B";

    public const string Reset = Esc + "[0m";
    public const string Bold = Esc + "[1m";
    public const string Dim = Esc + "[2m";
    public const string Italic = Esc + "[3m";

    public const string Red = Esc + "[31m";
    public const string Green = Esc + "[32m";
    public const string Yellow = Esc + "[33m";
    public const string Blue = Esc + "[34m";
    public const string Magenta = Esc + "[35m";
    public const string Cyan = Esc + "[36m";
    public const string Gray = Esc + "[90m";

    public const string BrightRed = Esc + "[91m";
    public const string BrightGreen = Esc + "[92m";
    public const string BrightYellow = Esc + "[93m";
    public const string BrightCyan = Esc + "[96m";
    public const string BrightWhite = Esc + "[97m";

    public static string Wrap(string text, string color) =>
        Enabled ? color + text + Reset : text;

    private static bool ResolveEnabled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }
        if (Console.IsOutputRedirected)
        {
            return false;
        }
        return true;
    }
}
