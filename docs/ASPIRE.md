## Running Archer with .NET Aspire

The repo ships an [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) AppHost that
orchestrates Archer for local development. One command launches the dashboard, the
embedded OTLP collector, and the TUI — with telemetry already wired through.

### What you get

Running the AppHost gives you:

- **Aspire dashboard** at `https://localhost:17181` (or `http://localhost:15181`) — the
  resources page, structured-log viewer, **distributed-trace viewer**, and metrics page.
- **Embedded OTLP collector** at `https://localhost:21181` — the dashboard *is* the
  collector; no Jaeger/Prometheus to install.
- **The TUI** launched in iTerm2 (or Terminal.app), with `OTEL_EXPORTER_OTLP_ENDPOINT`
  pre-injected so its traces, metrics, and logs flow into the dashboard automatically.

### Project layout

```
src/Aspire/
  Archer.AppHost.csproj    ← Aspire.AppHost.Sdk
  Program.cs               ← DistributedApplication wiring
  Properties/
    launchSettings.json    ← dashboard ports
  appsettings.json
```

The AppHost is registered in the solution under the **`/src/Orchestration/`** virtual
folder (separate from its physical `src/Aspire/` location) so it groups with the
"just run the thing" entry points in your IDE's solution explorer.

### Quickstart

From the repo root:

```bash
dotnet run --project src/Aspire
```

A browser tab opens at the Aspire dashboard. iTerm2 launches with the TUI. Type a prompt;
in the dashboard's **Traces** tab you'll see one trace per turn, expanding into
`archer.turn` → `archer.model.call` → `archer.tool.<name>` spans.

To run the AppHost from your IDE: pick the `https` (or `http`) launch profile in
`Archer.AppHost`'s Properties → **launchSettings.json**.

### What the AppHost configures

`src/Aspire/Program.cs` registers Archer.Tui as an *executable
resource* using `scripts/tui-debug.sh` as the launcher (the TUI needs a real PTY, so we
spawn iTerm2/Terminal.app rather than letting Aspire capture stdio):

```csharp
builder.AddExecutable(
        name: "archer-tui",
        command: tuiScript,
        workingDirectory: repoRoot,
        args: ["--repo", defaultRepo, "--terminal=iterm"])
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithOtlpExporter();
```

`WithOtlpExporter()` is the key call. Aspire injects the standard OTel env vars into the
spawned process:

| Env var | Purpose |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | dashboard's OTLP gRPC port |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` or `http/protobuf` |
| `OTEL_SERVICE_NAME` | resource service name (`archer-tui`) |
| `OTEL_RESOURCE_ATTRIBUTES` | extra resource attributes |

Archer's host code already honors these. From `src/Archer.Host/OtelConfig.cs`:

```csharp
// Honor the standard OTLP env var that .NET Aspire (and the OTel SDK at large)
// inject. Explicit Otel:Endpoint in config still wins.
if (string.IsNullOrWhiteSpace(opts.Endpoint))
{
    opts.Endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
}
```

So under Aspire you don't have to set anything in `appsettings.Development.json` — the
dashboard wires itself up automatically. Outside Aspire, the same `Otel:Endpoint`
configuration key still works for pointing at any other OTLP backend.

### Adding the CLI to the orchestration

The CLI is a project reference in the AppHost csproj but isn't started by default — most
CLI invocations are one-shot. To track a CLI invocation in the dashboard, set the same
env vars manually:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:19181 \
  dotnet run --project src/Archer.Cli -- agents
```

Or wire it as a second `AddExecutable` with `args: ["agents"]` and run it on demand from
the dashboard's resources view.

### Troubleshooting

**"`scripts/tui-debug.sh` not found".** The AppHost computes the repo root by walking up
two directories from `Directory.GetCurrentDirectory()`. If you're launching from a
non-default location, set `ARCHER_REPO_ROOT` and edit `Program.cs` to read it (the
default is fine for `dotnet run --project ...` from the repo root).

**TUI opens but no traces appear in the dashboard.** Check the iTerm tab for the line
`Repo: ...` — if it's there, the binary launched fine. Then verify the env injected:
inside the iTerm tab type `echo $OTEL_EXPORTER_OTLP_ENDPOINT` (well, you'd need to do
this before the TUI starts; Ctrl-C the TUI first). If empty, your Aspire version
predates `WithOtlpExporter()` — upgrade `Aspire.Hosting` to 13.x.

**Aspire dashboard says "no telemetry yet".** Send a prompt; the first telemetry arrives
when the agent commits its first turn. Tool calls are children of `archer.turn`, so an
empty trace tree means the agent is still in its first model call.

**HTTPS dashboard fails to load.** macOS may not trust the local dev cert. Run
`dotnet dev-certs https --trust` once. Or use the `http` launch profile.

### Why Aspire orchestrates Archer instead of just running it

Without Aspire you have three things to set up to get traces:

1. A docker-compose with Jaeger or Tempo + their UI.
2. An `Otel:Endpoint` in `appsettings.Development.json` pointing at the right port.
3. Restarting the TUI each time you tweak the OTel pipeline.

Aspire collapses those into one `dotnet run` and gives you a single web UI for traces,
logs, and metrics. The AppHost is also where additional resources will land as Archer
grows — sub-agent grains, an HTTP API surface, a Postgres for state when we outgrow the
file store, an external OTLP collector for shipping production telemetry — without
touching the runtime code.

### Further reading

- [docs/TELEMETRY.md](TELEMETRY.md) — span/metric inventory, tag keys, alternative non-Aspire setups.
- [docs/ARCHITECTURE.md](ARCHITECTURE.md) — how spans correspond to grain boundaries.
- [docs/CONFIGURATION.md](CONFIGURATION.md) — `Otel:` configuration section reference.
