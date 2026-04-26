// Aspire AppHost — orchestrates Archer for local development.
//
// What this gives you:
//   • One command (`dotnet run`) launches Archer plus the Aspire dashboard at http://localhost:18888.
//   • The dashboard hosts an OTLP collector, so traces/metrics/logs from Archer flow there
//     automatically — no Jaeger / Prometheus / Grafana to install.
//   • Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT into every child process; Archer.Host's
//     OtelConfig honors that env var.
//
// The Archer.Tui binary is a Terminal.Gui app and needs a real PTY, so we wire it as an
// "executable" resource that delegates to scripts/tui-debug.sh — that opens an iTerm2 (or
// Terminal.app) tab where the TUI actually renders. Aspire still captures the spawned
// process for liveness and tags everything with the right service name.
//
// The Archer CLI is registered separately so you can run scripted invocations with the same
// OTel pipeline by setting the environment variables manually:
//   OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 archer agents

using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Resolve the repo root so we can locate scripts/tui-debug.sh and the agents/ directory
// regardless of where the AppHost was launched from.
var repoRoot = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    "..", ".."));

var defaultRepo = repoRoot;
var sampleRepo = Path.Combine(repoRoot, "samples", "sample-repo");
if (Directory.Exists(sampleRepo))
{
    defaultRepo = sampleRepo;
}

// The TUI launcher script. It opens iTerm2/Terminal.app, exec's `dotnet ...archer-tui.dll`
// inside that real terminal so Terminal.Gui has a PTY. Aspire-injected OTLP env vars (set
// below via WithEnvironment) flow through the script's `export`.
var tuiScript = Path.Combine(repoRoot, "scripts", "tui-debug.sh");

string[] tuiArgs = ["--repo", defaultRepo, "--terminal=iterm"];
builder.AddExecutable(
        name: "archer-tui",
        command: tuiScript,
        workingDirectory: repoRoot,
        args: tuiArgs)
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // OTLP endpoint is auto-set by Aspire (OTEL_EXPORTER_OTLP_ENDPOINT). We additionally set
    // the OtelConfig section keys so the host code that reads `Otel:Endpoint` from
    // configuration also picks it up — both paths converge on the same dashboard.
    .WithOtlpExporter();

await builder.Build().RunAsync();

// Aspire AppHost entry point — orchestration code that boots the distributed application.
// Not testable from xUnit (the Aspire host expects to manage child processes).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static partial class Program { }
