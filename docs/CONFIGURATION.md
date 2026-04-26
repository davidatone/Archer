## Archer Configuration

Configuration for the Archer agent framework. Both the CLI (`Archer.Cli`) and TUI
(`Archer.Tui`) share the same host wiring in
[`src/Archer.Host/ArcherHostBuilder.cs:36-47`](../src/Archer.Host/ArcherHostBuilder.cs).

For per-agent profiles (model deployment, instructions, tools, context strategy)
see [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md). This document only covers
*connection-level* and *host-level* configuration.

### Configuration sources & load order

The host calls `ConfigureAppConfiguration` at
[`ArcherHostBuilder.cs:36-47`](../src/Archer.Host/ArcherHostBuilder.cs#L36-L47).
Sources are added in this order — later sources override earlier ones:

1. `appsettings.json` (optional, in `AppContext.BaseDirectory`)
2. `appsettings.{Environment}.json` (optional; `Environment` resolves from
   `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`, defaulting to `Production`,
   or `Development` when a debugger is attached — see lines 29-34)
3. Process environment variables (no prefix — bind via standard `Section:Key`
   notation, e.g. `AzureOpenAI__Endpoint`)
4. Environment variables with prefix `CODESCOUT_` (the prefix is stripped before
   binding — `CODESCOUT_AzureOpenAI__Endpoint` maps to `AzureOpenAI:Endpoint`)

> Note: only `CODESCOUT_` is registered as a prefix today
> ([`ArcherHostBuilder.cs:46`](../src/Archer.Host/ArcherHostBuilder.cs#L46)).
> If your environment exports `ARCHER_*` variables they will not be picked up
> automatically — rename them or add an extra `AddEnvironmentVariables(prefix:
> "ARCHER_")` call to the host builder.

In addition, four well-known Azure OpenAI variables are mirrored into the
`AzureOpenAI:*` section by `BindAzureOpenAIFromEnv` at
[`ArcherHostBuilder.cs:97-117`](../src/Archer.Host/ArcherHostBuilder.cs#L97-L117)
*after* the standard providers run, but they only fill keys that are still
unset (`root[key] ??= value` on line 114):

| Environment variable        | Maps to                       |
| --------------------------- | ----------------------------- |
| `AZURE_OPENAI_ENDPOINT`     | `AzureOpenAI:Endpoint`        |
| `AZURE_OPENAI_API_KEY`      | `AzureOpenAI:ApiKey`          |
| `AZURE_OPENAI_DEPLOYMENT`   | `AzureOpenAI:DefaultDeployment` |
| `AZURE_OPENAI_API_VERSION`  | `AzureOpenAI:ApiVersion`      |

### `AzureOpenAI`

Connection-level Azure OpenAI configuration. Per-agent settings (deployment,
reasoning, max-tokens) live on each `AgentDefinition`; the values here are
fall-back defaults used when a definition omits them or when an agent runs
without one. See
[`src/Archer.Model/AzureOpenAIOptions.cs:10-40`](../src/Archer.Model/AzureOpenAIOptions.cs#L10-L40).

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://my-resource.openai.azure.com/",
    "ApiKey": "<key-or-omit-for-AzureCliCredential>",
    "DefaultDeployment": "gpt-5-mini",
    "ApiVersion": "2025-04-01-preview",
    "UseV1Surface": true,
    "MaxCompletionTokens": 16384,
    "ReasoningEffort": "Medium",
    "ReasoningSummary": "Auto"
  }
}
```

| Field                 | Type   | Default            | Notes |
| --------------------- | ------ | ------------------ | ----- |
| `Endpoint`            | string | `null`             | Resource URL, e.g. `https://my-resource.openai.azure.com/`. Required for any real call ([line 15](../src/Archer.Model/AzureOpenAIOptions.cs#L15)). |
| `ApiKey`              | string | `null`             | API key. If `null`, the SDK falls back to `AzureCliCredential` / `DefaultAzureCredential` ([line 17](../src/Archer.Model/AzureOpenAIOptions.cs#L17)). Override only when keyless auth isn't viable. |
| `DefaultDeployment`   | string | `gpt-5-mini`       | Default model when an agent definition omits its own ([line 21](../src/Archer.Model/AzureOpenAIOptions.cs#L21)). |
| `ApiVersion`          | string | `null`             | Azure API version (e.g. `2025-04-01-preview`). `null` lets the SDK pick ([line 24](../src/Archer.Model/AzureOpenAIOptions.cs#L24)). |
| `UseV1Surface`        | bool   | `true`             | When `true`, requests route to `/openai/v1/responses` with the model in the body. When `false`, legacy deployments-style routing ([line 30](../src/Archer.Model/AzureOpenAIOptions.cs#L30)). |
| `MaxCompletionTokens` | int    | `16384`            | Fall-back completion-token cap when the agent definition doesn't override ([line 33](../src/Archer.Model/AzureOpenAIOptions.cs#L33)). |
| `ReasoningEffort`     | enum   | `Medium`           | One of `None`, `Minimal`, `Low`, `Medium`, `High` ([`AgentDefinition.cs:39-46`](../src/Archer.Domain/Agents/AgentDefinition.cs#L39-L46)). |
| `ReasoningSummary`    | enum   | `Auto`             | One of `Auto`, `Concise`, `Detailed` ([`AgentDefinition.cs:48-53`](../src/Archer.Domain/Agents/AgentDefinition.cs#L48-L53)). |

**v1 surface vs. legacy deployments routing.** Set `UseV1Surface: true` (the
default, [line 30](../src/Archer.Model/AzureOpenAIOptions.cs#L30)) when your
resource exposes the new responses API at `/openai/v1/responses` and you pass
the deployment as a request-body field. Set `UseV1Surface: false` for older
resources where the deployment must appear in the URL path
(`/openai/deployments/{name}/...`). Most new Azure OpenAI resources accept the
v1 surface; only flip the switch if you see 404s or "deployment not found"
errors with the default.

### `Persistence`

Where Archer writes durable agent state.
[`src/Archer.Persistence/FileAgentStateStoreOptions.cs:3-9`](../src/Archer.Persistence/FileAgentStateStoreOptions.cs#L3-L9).

```json
{ "Persistence": { "StateDirectory": ".archer" } }
```

| Field            | Type   | Default   | Notes |
| ---------------- | ------ | --------- | ----- |
| `StateDirectory` | string | `.archer` | Root directory for serialized agent state, resolved relative to the host's working directory. Override per-environment if you want CI to use a scratch dir, or to share state across users. |

### `Tools`

Knobs for the built-in tools (`list_files`, `grep`, `search_pattern`,
`todo_list`).
[`src/Archer.Tools/ToolOptions.cs:3-39`](../src/Archer.Tools/ToolOptions.cs#L3-L39).

```json
{
  "Tools": {
    "DefaultExcludeGlobs": [".git/**", "bin/**", "obj/**", "node_modules/**"],
    "MaxFileBytes": 1048576,
    "MaxToolResultBytes": 256000,
    "FollowSymlinks": false,
    "PreferredPathPrefixes": ["src/", "lib/", "app/"]
  }
}
```

| Field                   | Type     | Default | Notes |
| ----------------------- | -------- | ------- | ----- |
| `DefaultExcludeGlobs`   | string[] | `[".git/**", "bin/**", "obj/**", "node_modules/**", "dist/**", "build/**", "coverage/**", "*.min.js", "*.lock", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.ico", "*.pdf", "*.zip"]` ([lines 8-26](../src/Archer.Tools/ToolOptions.cs#L8-L26)) | Applied to every tool call unless the call passes an explicit exclude list. Override to add language-specific build dirs (`target/**`, `out/**`, ...). |
| `MaxFileBytes`          | int      | `1048576` (1 MB) | Hard cap on individual file reads. Files exceeding this are truncated. |
| `MaxToolResultBytes`    | int      | `256000` | Hard cap on serialized tool-result payload returned to the model. |
| `FollowSymlinks`        | bool     | `false`  | Off by default — symlinks can escape the repo root or create cycles. Flip on only for trusted repos. |
| `PreferredPathPrefixes` | string[] | `["src/", "lib/", "app/"]` ([line 38](../src/Archer.Tools/ToolOptions.cs#L38)) | Path-prefix bonuses applied by `search_pattern`'s ranking. Files whose repo-relative path starts with one of these get the full bonus (1.0), unmatched paths get 0.6. Useful for biasing toward source dirs regardless of language. |

### `TurnWorker`

The per-turn execution budget. Defined on
[`TurnWorkerGrain.cs:287-293`](../src/Archer.Actors/Grains/TurnWorkerGrain.cs#L287-L293).

```json
{ "TurnWorker": { "MaxIterations": 9999 } }
```

| Field           | Type | Default | Notes |
| --------------- | ---- | ------- | ----- |
| `MaxIterations` | int  | `9999`  | Soft cap on tool-call iterations within a single turn. When exceeded the turn fails with `"Max tool iterations exceeded without a final answer."` ([line 254](../src/Archer.Actors/Grains/TurnWorkerGrain.cs#L254)). `9999` is "effectively unlimited"; set to a smaller value (e.g. `8` or `16`) in CI to fail-fast on runaway agents. |

### `Otel`

OpenTelemetry traces, metrics, and logs. Defined on
[`OtelConfig.cs:23-38`](../src/Archer.Host/OtelConfig.cs#L23-L38). When no
`Endpoint` is set the registration is a no-op (telemetry is collected in-proc
but not shipped) — see comment at
[`OtelConfig.cs:14-19`](../src/Archer.Host/OtelConfig.cs#L14-L19).

```json
{
  "Otel": {
    "ServiceName": "Archer",
    "Endpoint": "http://localhost:4317",
    "Protocol": "grpc",
    "ConsoleExporter": false
  }
}
```

| Field             | Type   | Default  | Notes |
| ----------------- | ------ | -------- | ----- |
| `ServiceName`     | string | `Archer` | Reported on the OTel resource ([line 28](../src/Archer.Host/OtelConfig.cs#L28)). |
| `Endpoint`        | string | `null`   | OTLP endpoint URL. When set, the OTLP exporter is enabled for traces, metrics, and logs ([lines 63-70, 81-88, 102-109](../src/Archer.Host/OtelConfig.cs)). |
| `Protocol`        | string | `grpc`   | Either `grpc` or `httpprotobuf` (case-insensitive). Anything other than `httpprotobuf` falls back to gRPC ([lines 116-119](../src/Archer.Host/OtelConfig.cs#L116-L119)). |
| `ConsoleExporter` | bool   | `false`  | When `true`, also writes spans, metrics, and logs to stdout — useful when you don't have a collector running. |

#### Local Otel quickstart

Run a local Jaeger via Docker (gRPC OTLP on 4317, UI on 16686):

```bash
docker run --rm -d --name jaeger \
  -p 16686:16686 -p 4317:4317 -p 4318:4318 \
  jaegertracing/all-in-one:1.62
```

Then in `appsettings.Development.json`:

```json
{ "Otel": { "Endpoint": "http://localhost:4317", "Protocol": "grpc" } }
```

For the .NET Aspire dashboard (also a turnkey OTLP receiver), use HTTP/proto:

```bash
docker run --rm -d --name aspire-dashboard \
  -p 18888:18888 -p 4318:18889 \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

```json
{ "Otel": { "Endpoint": "http://localhost:4318", "Protocol": "httpprotobuf" } }
```

For zero-infra debugging, just set `"ConsoleExporter": true` and skip Docker.

### `Logging`

Standard `Microsoft.Extensions.Logging` configuration. The host registers
`SimpleConsole` at `Information` minimum
([`ArcherHostBuilder.cs:79-88`](../src/Archer.Host/ArcherHostBuilder.cs#L79-L88));
override per-namespace through `Logging:LogLevel`.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Orleans": "Warning",
      "Archer": "Debug"
    }
  }
}
```

Recognized levels: `Trace`, `Debug`, `Information`, `Warning`, `Error`,
`Critical`, `None`. Useful namespace overrides:

- `Archer` — all framework logs (turn worker, registry, model runner)
- `Archer.Persistence.Agents.AgentDefinitionRegistry` — hot-reload events
- `Microsoft.Orleans.Runtime` / `Orleans` — silo internals (verbose at `Information`)
- `Azure.Core` — Azure SDK retries (drop to `Warning` or `Error` in noisy envs)

### Complete example: `appsettings.Development.json`

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource>.openai.azure.com/",
    "ApiKey": "<placeholder-or-omit-for-AzureCliCredential>",
    "DefaultDeployment": "gpt-5-mini",
    "ApiVersion": "2025-04-01-preview",
    "UseV1Surface": true,
    "MaxCompletionTokens": 16384,
    "ReasoningEffort": "High",
    "ReasoningSummary": "Auto"
  },
  "Persistence": {
    "StateDirectory": ".archer"
  },
  "Tools": {
    "DefaultExcludeGlobs": [
      ".git/**", "bin/**", "obj/**", "node_modules/**",
      "dist/**", "build/**", "coverage/**",
      "*.min.js", "*.lock"
    ],
    "MaxFileBytes": 1048576,
    "MaxToolResultBytes": 256000,
    "FollowSymlinks": false,
    "PreferredPathPrefixes": ["src/", "lib/", "app/"]
  },
  "TurnWorker": {
    "MaxIterations": 16
  },
  "Otel": {
    "ServiceName": "Archer.Dev",
    "Endpoint": "http://localhost:4317",
    "Protocol": "grpc",
    "ConsoleExporter": false
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Orleans": "Warning",
      "Archer": "Debug"
    }
  }
}
```

Notes:

- The CLI ships a real `appsettings.Development.json` at
  `src/Archer.Cli/appsettings.Development.json`. Copy from this template and
  fill in real values; never commit secrets.
- For local dev without an API key, omit `ApiKey` and run `az login` so
  `AzureCliCredential` can mint tokens
  ([`AzureOpenAIOptions.cs:17`](../src/Archer.Model/AzureOpenAIOptions.cs#L17)).
- Per-environment overrides win — e.g. ship a defaults `appsettings.json` with
  `Otel:ConsoleExporter: false` and override to `true` in
  `appsettings.Development.json`.

### See also

- [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md) — YAML schema for agent
  profiles (per-agent `model`, `tools`, `context`, `interruption`).
