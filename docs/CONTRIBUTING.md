## Contributing to Archer

This is the practical guide for working on Archer locally: build, run tests,
generate coverage, push through SonarCloud, and what the quality gate means
before you open a PR.

For architecture and code structure see [ARCHITECTURE.md](./ARCHITECTURE.md);
for the agent/yaml model see [AGENT_DEFINITIONS.md](./AGENT_DEFINITIONS.md).

### Prerequisites

- **.NET 10 SDK** (per `global.json`)
- **`dotnet-sonarscanner`** for Sonar runs:
  ```bash
  dotnet tool install --global dotnet-sonarscanner --version 11.2.1
  ```
- **`reportgenerator`** for human-readable coverage reports (optional):
  ```bash
  dotnet tool install --global dotnet-reportgenerator-globaltool
  ```
- **`SONAR_TOKEN`** environment variable for SonarCloud uploads (token kept in
  `~/.zshrc`; rotate via https://sonarcloud.io/account/security if it leaks).

### Build and test

```bash
# Clean build of the whole solution
dotnet build Archer.slnx

# Run every test project
dotnet test Archer.slnx
```

Each test project lives under `tests/Archer.<Layer>.Tests/` and has its own
`.csproj`. There's no per-PR fast/slow split — all tests run on every change
and the suite finishes in well under a minute (the Orleans TestCluster startup
in `Archer.Actors.Tests` accounts for most of the wall time).

### Coverage

Coverage is collected with **coverlet**, configured in
[`coverlet.runsettings`](../coverlet.runsettings). The notable exclusions:

- `tests/**` — test projects don't count toward coverage
- `**/obj/**` — generated code
- `[archer-tui]Program`, `[Archer.AppHost]Program` — top-level statements that
  bootstrap the host
- Anything marked `[ExcludeFromCodeCoverage]` (most UI code in `Archer.Tui`,
  CLI command handlers in `Archer.Cli`, and Aspire's `Program`)

#### One-shot: run + report

```bash
# 1. Run every test project with the runsettings, dropping fresh coverage XML
find tests -name TestResults -type d -exec rm -rf {} + 2>/dev/null
for d in tests/Archer.*.Tests; do
  dotnet test $d --collect:"XPlat Code Coverage" --settings coverlet.runsettings --nologo
done

# 2. Stitch the per-project reports into one HTML
reportgenerator \
  -reports:"tests/**/coverage.cobertura.xml" \
  -targetdir:coverage-report \
  -reporttypes:Html\;TextSummary

# 3. Read the headline numbers
head -20 coverage-report/Summary.txt
# … or open the HTML
open coverage-report/index.html
```

#### What's expected

The current numbers (top of session, fluctuates ±1%):

| Metric | Project total |
|--------|---------------|
| Line coverage | ~88% |
| Branch coverage | ~76% |
| Method coverage | ~95% |

Per-assembly: most non-UI projects are 90%+; `archer-tui` is intentionally
low (~35%) because Terminal.Gui v2 cannot initialise inside the xUnit test
process — the testable seams (`EventRenderer`, `TextRenderer`, `MainWindowHelpers`,
`ServersDialog.Format*`, `MarkdownView` parsers) are at 100% and the UI shells
are `[ExcludeFromCodeCoverage]`. See [TUI.md § Pure-logic helpers](./TUI.md).

### Sonar scan

The project is registered as `scottturner_archer` on
[sonarcloud.io](https://sonarcloud.io/dashboard?id=scottturner_archer). The
quality gate requires:

- 0 bugs / 0 vulnerabilities (rating A)
- new-code coverage ≥ 80%
- duplication ≤ 3% on new code
- 100% security hotspots reviewed

Scan from local:

```bash
# 1. Begin — captures analysis config + writes .sonarqube/conf
dotnet sonarscanner begin \
  /k:"scottturner_archer" \
  /o:"scottturner" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.host.url="https://sonarcloud.io" \
  /d:sonar.cs.opencover.reportsPaths="tests/**/coverage.opencover.xml" \
  /d:sonar.scm.disabled=true \
  /d:sonar.exclusions="**/bin/**,**/obj/**,coverage-out/**,coverage-report/**,coverage-html/**,.codescout/**,.archer/**,.sonarqube/**" \
  /d:sonar.coverage.exclusions="tests/**"

# 2. Build (the scanner instruments compilation)
dotnet build Archer.slnx --nologo

# 3. Run tests with coverage, freshly
find tests -name TestResults -type d -exec rm -rf {} + 2>/dev/null
for d in tests/Archer.*.Tests; do
  dotnet test $d --collect:"XPlat Code Coverage" --settings coverlet.runsettings \
    --nologo --no-build
done

# 4. End — uploads to SonarCloud
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
```

#### Why the exclusions matter

- **Without `sonar.exclusions`** the scanner would walk the whole working tree —
  including `.codescout/` (real agent run logs from this repo's own dev usage),
  `coverage-report/` (generated HTML), and `coverage-out/` (old XML). That's
  ~250k extra "lines" of artefacts and would push the project past SonarCloud's
  free-tier quota. The exclusions pin the scan to source code only (~9.3k LOC).
- **`sonar.coverage.exclusions=tests/**`** is so the test projects themselves
  don't show up as "uncovered code".
- **`sonar.scm.disabled=true`** is a workaround for the project not being a git
  repo when Sonar's SCM detector ran — change once the repo has a remote.

#### Inspecting the result

```bash
# Wait for the analysis task and check the gate
TASK_ID=$(curl -s -u "$SONAR_TOKEN:" \
  "https://sonarcloud.io/api/ce/activity?component=scottturner_archer&onlyCurrents=true&ps=1" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['tasks'][0]['id'])")

until [ "$(curl -s -u "$SONAR_TOKEN:" \
  "https://sonarcloud.io/api/ce/task?id=$TASK_ID" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['task']['status'])")" \
  != "IN_PROGRESS" ]; do sleep 3; done

curl -s -u "$SONAR_TOKEN:" \
  "https://sonarcloud.io/api/qualitygates/project_status?projectKey=scottturner_archer" \
  | python3 -m json.tool
```

The dashboard URL is logged at the end of `sonarscanner end`.

### When the gate fails

| Failure | What it means | First thing to check |
|---------|---------------|----------------------|
| `new_coverage < 80` | New / changed lines aren't well-tested | Open the SonarCloud "New Code" tab; the coloured bar shows uncovered new lines per file. |
| `bugs > 0` | New bug-class issue introduced | `curl …/api/issues/search?…&types=BUG` — fix or justify (some Roslyn rules are noise). |
| `vulnerabilities > 0` | Real or claimed vulnerability | Don't dismiss without root-cause; vulnerabilities are tracked separately from hotspots. |
| `security_hotspots_reviewed < 100` | A new hotspot needs a human verdict | Mark **Safe** (with a comment) or **Fixed** (after addressing) in the SonarCloud UI; don't auto-dismiss. |
| `code_smells > 0` | Often a false-positive Roslyn rule | Many test-project smells (CA1816, CA1861) are suppressed via NoWarn in `Directory.Build.props`. |

### Coding conventions

The project enforces conventions via Roslyn analyzers; the build fails on most
violations because `TreatWarningsAsErrors` is set in `Directory.Build.props`.
Suppressed-by-design rules (also in `Directory.Build.props`):

| Rule | Why suppressed |
|------|---------------|
| CA1707 | We use `_` in test method names for readability |
| CA1716 | Reserved-keyword identifier rule — too pedantic for our domain types |
| CA2007 | `.ConfigureAwait(false)` not needed in app code (no SynchronizationContext) |
| CA1848, CA1873 | LoggerMessage source generators add noise without measurable benefit |

In test projects only (test-project condition in `Directory.Build.props`):

| Rule | Why suppressed |
|------|---------------|
| CA1861 | Constant-array hoisting; tests run once, allocation cost is trivial |

### Pre-commit checklist

Before opening a PR:

1. `dotnet build Archer.slnx` — must finish with **0 warnings**
   (`TreatWarningsAsErrors` will fail the build otherwise).
2. `dotnet test Archer.slnx` — every test project passes (transient `Mcp.Tests`
   port-collision flakes are the one known sad path; rerun once if you see
   `BrowserAuthFlowIntegrationTests` fail).
3. Run a Sonar scan locally if your change touches non-trivial code; verify the
   gate is **OK**.
4. If you added a new public type or API, update the doc that owns that area
   (this repo's docs are kept in sync with file:line refs — `git grep` for
   adjacent symbols and update the matching md file).

### Repository layout for new contributors

| Directory | What's there |
|-----------|--------------|
| `src/Archer.Domain` | DTOs and value types (`AgentDefinition`, `AgentMessage`, `ToolResult`, …) |
| `src/Archer.Application` | Interfaces (`IAgentEventSink`, `IAgentStateStore`, `IModelTurnRunner`, …) |
| `src/Archer.Persistence` | File-backed implementations of state and definition stores |
| `src/Archer.Events` | The default `ChannelAgentEventSink` + persisting decorator |
| `src/Archer.Tools` | Built-in tools (`list_files`, `grep`, `search_pattern`, `todo_list`) |
| `src/Archer.Mcp` | MCP server registry, OAuth client, credential store, tool source |
| `src/Archer.Model` | Azure OpenAI chat-client factory + agent-framework turn runner |
| `src/Archer.Actors` | Orleans grains (`ArcherAgentGrain`, `TurnWorkerGrain`) |
| `src/Archer.Host` | DI wiring, OTel config, Orleans silo bootstrap |
| `src/Archer.Cli` | `archer …` CLI (System.CommandLine) |
| `src/Archer.Tui` | Terminal.Gui v2 front-end |
| `src/Aspire` | .NET Aspire AppHost for local dev orchestration |
| `tests/Archer.<Layer>.Tests` | xUnit test projects, one per source layer |
| `agents/*.yaml` | Built-in agent definitions |
| `mcp/*.yaml` | Built-in MCP server configurations |
| `docs/*.md` | This documentation set |
| `docs/proposals/*` | RFC-style proposals before implementation begins |
| `scripts/*.sh` | Dev helper scripts (e.g. `tui-debug.sh`) |

### See also

- [ARCHITECTURE.md](./ARCHITECTURE.md) — layered project map
- [INTERNALS.md](./INTERNALS.md) — turn lifecycle, fencing, MCP startup, persistence
- [TUI.md](./TUI.md) — TUI internals + the testable-seam pattern that keeps coverage honest
- [docs/proposals/multi-agent-sdlc/README.md](./proposals/multi-agent-sdlc/README.md) — RFC for multi-agent SDLC workflows
