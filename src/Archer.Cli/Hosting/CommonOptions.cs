using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Archer.Cli.Hosting;

/// <summary>
/// Cross-command options and the lambdas that turn them into host configuration overrides.
/// Centralized so every CLI command exposes the same flags consistently.
/// </summary>
internal static class CommonOptions
{
    /// <summary>Override the persistent state directory (default: <c>.archer</c>).</summary>
    public static Option<string?> StateDir { get; } = new(
        "--state-dir",
        "Override the .archer state directory.");

    /// <summary>
    /// Build a <see cref="Microsoft.Extensions.Hosting"/> configure-services delegate that
    /// applies the supplied state-dir override to <c>Persistence:StateDirectory</c>. Returns
    /// null when no override was given (so callers can pass it straight through).
    /// </summary>
    public static Action<HostBuilderContext, IServiceCollection>? StateDirOverride(string? stateDir) =>
        stateDir is null ? null : (ctx, _) =>
        {
            ctx.Configuration["Persistence:StateDirectory"] = stateDir;
        };
}
