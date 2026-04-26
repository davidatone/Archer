using System.Text;
using Archer.Application.Events;
using Archer.Domain.Agents;
using Archer.Events;
using Archer.Host;
using Archer.Persistence;
using Archer.Tui;
using Archer.Tui.Sessions;
using Archer.Tui.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Terminal.Gui;
using TGApp = Terminal.Gui.Application;

// --check-layout mode: boot the TUI under FakeDriver, render one frame, dump the cell buffer,
// and exit. Used to verify layout/focus/visibility from CI or scripts without a real TTY.
if (args.Contains("--check-layout"))
{
    return CheckLayout();
}

// Refuse to start without a real terminal. IDE "Run" panels (Rider, VS) pipe stdio
// through non-TTY streams; Terminal.Gui can't drive them and will crash the host.
if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    await Console.Error.WriteLineAsync("""
        archer-tui requires a real terminal (TTY) to run.

        Detected redirected stdin/stdout — likely launched from an IDE Run window.

        Fixes:
          • In Rider/VS: open the Run/Debug Configuration and enable
            "Use external terminal" (or set "useExternalConsole": true in
            Properties/launchSettings.json). Then re-launch.
          • Alternatively, run from a real terminal:
              dotnet run --project src/Archer.Tui -- --repo <path>
          • For a non-TUI experience inside the IDE, use the regular CLI:
              dotnet run --project src/Archer.Cli
        """).ConfigureAwait(false);
    return 2;
}

string? repoArg = null;
for (var i = 0; i < args.Length; i++)
{
    if ((args[i] == "--repo" || args[i] == "-r") && i + 1 < args.Length)
    {
        repoArg = args[i + 1];
    }
}

var repo = repoArg is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(repoArg);
if (!Directory.Exists(repo))
{
    await Console.Error.WriteLineAsync($"--repo points to a directory that doesn't exist: {repo}").ConfigureAwait(false);
    await Console.Error.WriteLineAsync($"  CWD: {Directory.GetCurrentDirectory()}").ConfigureAwait(false);
    await Console.Error.WriteLineAsync("  Pass an absolute path or run from the project root.").ConfigureAwait(false);
    return 2;
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

// Route logs to a file inside the state dir so the TUI screen stays clean.
// Orleans is chatty (especially around grain-call deadlines for long model turns) and
// any console writer would scribble over the rendered UI.
var logDir = Path.Combine(repo, ".archer", "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"tui-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
var logStream = new StreamWriter(logPath, append: true) { AutoFlush = true };

var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
    .ConfigureArcher((_, services) =>
    {
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Warning);
            // Squelch Orleans' "expired message" deadline warnings — model turns commonly
            // run longer than the default 30s deadline and the warnings aren't actionable.
            b.AddFilter("Orleans.Messaging", LogLevel.Error);
            b.AddFilter("Orleans.Runtime.Messaging", LogLevel.Error);
            b.AddFilter("Orleans.Runtime.GrainTimer", LogLevel.Error);
            b.AddProvider(new TuiFileLoggerProvider(logStream));
        });
    });

using var host = hostBuilder.Build();
await host.StartAsync(lifetime.Token);

try
{
    TGApp.Init();
    try
    {
        var top = new MainWindow(host.Services, repo);
        TGApp.Run(top);
        top.Dispose();
    }
    finally
    {
        TGApp.Shutdown();
    }
}
finally
{
    await host.StopAsync();
}

return 0;

static int CheckLayout()
{
    TGApp.Init(new FakeDriver());
    try
    {
        var top = BuildCheckLayoutTop(out var tmp);
        TGApp.Begin(top);
        TGApp.LayoutAndDraw();
        DumpLayoutDiagnostics(top);
        try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
        return 0;
    }
    finally
    {
        TGApp.Shutdown();
    }
}

static Toplevel BuildCheckLayoutTop(out string tmp)
{
    tmp = Path.Combine(Path.GetTempPath(), "archer-tui-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    var session = new AgentSession(
        AgentId.New(), tmp, "code-scout",
        grainResolver: () => null!, new ChannelAgentEventSink());
    var top = new Toplevel();
    top.Add(new AgentTabView(session));
    return top;
}

static void DumpLayoutDiagnostics(Toplevel top)
{
    var driver = TGApp.Driver!;
    Console.WriteLine($"--- buffer {driver.Cols}x{driver.Rows} ---");
    DumpBuffer(driver);
    Console.WriteLine($"--- cursor: ({driver.Col}, {driver.Row}) ---");
    Console.WriteLine($"--- focused: {DescribeFocus(top)} ---");
    Console.WriteLine("--- attribute samples ---");
    SampleAttribute(driver, "Chat body", 5, 5);
    SampleAttribute(driver, "Events body", 11, 50);
    SampleAttribute(driver, "Todos body", 5, 50);
    SampleAttribute(driver, "Input row", 23, 5);
    SampleAttribute(driver, "Status row", 24, 5);
    Console.WriteLine("--- view tree ---");
    DescribeTree(top, 0);
}

static void DumpBuffer(Terminal.Gui.IConsoleDriver driver)
{
    for (var r = 0; r < driver.Rows; r++)
    {
        var sb = new StringBuilder();
        sb.Append($"{r,3}: ");
        for (var c = 0; c < driver.Cols; c++)
        {
            var rune = driver.Contents![r, c].Rune;
            var ch = rune.Value == 0 ? ' ' : (char)rune.Value;
            if (ch < 32 || ch > 126) ch = '·';
            sb.Append(ch);
        }
        Console.WriteLine(sb.ToString());
    }
}

static void SampleAttribute(Terminal.Gui.IConsoleDriver d, string label, int row, int col)
{
    if (row >= d.Rows || col >= d.Cols) { Console.WriteLine($"  {label}: (out of bounds)"); return; }
    var cell = d.Contents![row, col];
    var a = cell.Attribute;
    if (a is null) { Console.WriteLine($"  {label} @ ({row},{col}): (no attribute)"); return; }
    var av = a.Value;
    Console.WriteLine($"  {label,-12} @ ({row},{col})  fg={av.Foreground}  bg={av.Background}");
}

static string DescribeFocus(View v)
{
    var f = v.MostFocused ?? (v.HasFocus ? v : null);
    return f is null ? "(none)" : $"{f.GetType().Name} canFocus={f.CanFocus} hasFocus={f.HasFocus} frame={f.Frame}";
}

static void DescribeTree(View v, int depth)
{
    Console.WriteLine($"{new string(' ', depth * 2)}{v.GetType().Name} canFocus={v.CanFocus} hasFocus={v.HasFocus} frame={v.Frame}");
    foreach (var s in v.Subviews)
    {
        DescribeTree(s, depth + 1);
    }
}

// The TUI entry point is a Terminal.Gui Application boot — it requires a real driver and
// can't be exercised from xUnit. Mark the synthetic Program class generated from these
// top-level statements as excluded from coverage.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static partial class Program { }
