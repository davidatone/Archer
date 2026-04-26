## Archer TUI Guide

`archer-tui` is the Terminal.Gui v2 front-end for the Archer agent framework. It
shares the same Orleans host and grain interfaces as the [CLI](./CLI.md) — the
TUI is a presentation layer over `IArcherAgentGrain` plus the
`IAgentEventSink` event stream.

The implementation lives in `src/Archer.Tui/`. The four files that matter for
this document:

- `Program.cs` — bootstrap, TTY guard, log routing, `--check-layout` mode
- `Ui/MainWindow.cs` — menu bar, tab strip, status bar, dialogs
- `Ui/AgentTabView.cs` — the chat / todos / events / prompt layout per tab
- `Ui/MarkdownView.cs` — read-only Markdown renderer used for the chat pane
- `Ui/Theme.cs` — color schemes and the `MarkdownView.Palette`

### Launching

Two supported entrypoints:

1. From a real terminal:

   ```bash
   dotnet run --project src/Archer.Tui -- --repo <path>
   ```

   `--repo` defaults to the current working directory if omitted
   (`Program.cs:55`). The path must be a real directory or the process exits
   with code `2` (`Program.cs:56-62`).

2. Via the helper script `scripts/tui-debug.sh`. This builds, kills any
   existing `archer-tui` processes, and opens a fresh iTerm2 (or Terminal.app)
   tab so you get a real TTY when launching from Rider/VS:

   ```bash
   scripts/tui-debug.sh                       # current dir
   scripts/tui-debug.sh --repo /path/to/repo
   scripts/tui-debug.sh --wait                # pause for debugger attach
   scripts/tui-debug.sh --terminal=iterm      # force iTerm2
   ```

#### TTY guard

Terminal.Gui cannot drive non-TTY pipes. The TUI **refuses to start** when
either stdin or stdout is redirected (`Program.cs:27-44`):

```
archer-tui requires a real terminal (TTY) to run.

Detected redirected stdin/stdout — likely launched from an IDE Run window.
…
```

When you see this, either run from a real terminal, enable "Use external
terminal" in your IDE's run configuration, or use the regular CLI
(`dotnet run --project src/Archer.Cli`).

#### Headless `--check-layout`

`Program.cs:20-23` triggers a special diagnostic mode:

```bash
dotnet run --project src/Archer.Tui -- --check-layout
```

This boots the TUI under Terminal.Gui's `FakeDriver`, lays out one frame, and
dumps the cell buffer plus focus state and view tree to stdout
(`Program.cs:119-201`). It writes a per-cell character grid, attribute samples
for each pane (Chat body, Events body, Todos body, Input row, Status row), the
cursor position, and the focused view. Use it from CI or scripts to verify that
the layout still places focus in the input field after refactors — no real
terminal required.

#### Logging

The TUI reroutes all `ILogger` output to a per-launch file so the rendered UI
stays clean (`Program.cs:74-93`). Logs go to:

```
<repo>/.archer/logs/tui-yyyyMMdd-HHmmss.log
```

Orleans' chatty deadline messages are filtered down to `Error` so long model
turns don't pollute the file (`Program.cs:88-90`). Nothing is ever written to
stdout/stderr while the TUI is running.

### Window layout

`AgentTabView` (`src/Archer.Tui/Ui/AgentTabView.cs:14`) lays out one tab as:

```
┌── Chat (60% width) ──────────┐┌── Todos (40% width × 40% height) ────┐
│                              ││ ☐ todo title                          │
│  you> …                      ││ ◐ doing item                          │
│  agent> …                    │└───────────────────────────────────────┘
│  ┄ thinking ┄                │┌── Events / reasoning ─────────────────┐
│  ▸ Header                    ││ ⏵ turn 0001                            │
│    body…                     ││ ✦ gpt-5.3-codex                        │
│                              ││ 🔧 list_files {"path":"."}             │
│                              ││    ↳ 47 entries                        │
└──────────────────────────────┘└────────────────────────────────────────┘
Prompt — type a question, press Enter:
[ input field                                                            ]
 scout_xxxxxxxxxxxx  •  turn:abc123 thinking…  •  msgs:7  •  todos:3
```

Frames and dimensions are set at `AgentTabView.cs:38-114`:

- **Chat pane** — `MarkdownView`, 60% width, fills height minus 4 rows reserved
  for prompt label + input + status (`AgentTabView.cs:38-53`).
- **Todos pane** — `ListView`, top-right, 40% height (`AgentTabView.cs:55-72`).
- **Events / reasoning pane** — read-only `TextView`, bottom-right, fills the
  remaining right column (`AgentTabView.cs:74-91`).
- **Prompt input** — `TextField` (no wrapping FrameView; wrappers in
  Terminal.Gui v2 swallow mouse events, see comment at
  `AgentTabView.cs:94-95`). Anchored to the bottom 2 rows.
- **Status bar** — agent id, active turn id (truncated), thinking indicator,
  message count, todo count (`AgentTabView.cs:341-346`).

The whole tab is `CanFocus = true` so focus can traverse into the input
(`AgentTabView.cs:35`); the input grabs focus once the view is initialized
(`AgentTabView.cs:131`).

### Tabs and the main window

`MainWindow` (`src/Archer.Tui/Ui/MainWindow.cs:13`) hosts a `MenuBar`, a
`TabView` (one tab per agent session), and a `StatusBar`. Each tab gets a label
of the form ` xxxxxx ` — six characters of the agent id, slice 6..12
(`MainWindow.cs:113`). When the selected tab changes, focus is invoked back to
the new tab's input (`MainWindow.cs:67-73`).

### Keyboard shortcuts

Defined in `MainWindow.cs:75-82`:

| Key            | Action                                                           |
|----------------|------------------------------------------------------------------|
| **F1**         | New agent in the default repo (`OpenNewAgentTab`)                |
| **Shift-F1**   | New agent — opens a dialog to pick agent type and repo path      |
| **F2**         | Open existing agent (list of saved ids)                          |
| **F3**         | Interrupt the active turn on the current tab                     |
| **Alt-F10**    | Quit                                                             |
| **Tab**        | Cycle focus through tab → chat → todos → events → input          |
| **Mouse wheel**| Scroll the chat pane (`MarkdownView.OnMouseEvent` lines 49-62)   |
| **Up/Down**    | Scroll chat by one line when the chat pane has focus             |
| **PgUp/PgDn**  | Scroll chat by one viewport (`MarkdownView.OnKeyDown` lines 64-76)|
| **Home/End**   | Scroll chat to top/bottom                                        |
| **Enter**      | Send the input as a user message (`AgentTabView.cs:148-162`)     |

The same actions are available from the **File** and **Agent** menus
(`MainWindow.cs:37-58`): `File → New agent`, `New agent in other repo…`,
`Open existing…`, `Quit`; `Agent → Interrupt`, `Refresh`.

### The new-agent dialog

`Shift-F1` opens `PromptForRepoAndOpen` (`MainWindow.cs:175-267`):

```
┌── New agent ──────────────────────────────────────┐
│ Agent type:                                       │
│ ┌─────────────────────────────────────────────┐   │
│ │ code-scout                                  │   │
│ │ …                                           │   │
│ └─────────────────────────────────────────────┘   │
│ Repository path (absolute or relative…):          │
│ [ /Users/me/code/myproj                       ]   │
│                                                   │
│ <error message slot>                              │
│                       [ Open ]   [ Cancel ]       │
└───────────────────────────────────────────────────┘
```

The agent type list is populated from
`IAgentDefinitionRegistry.All` (`MainWindow.cs:177`) — every YAML in `agents/`
shows up. Validation happens on `Open` (`MainWindow.cs:233-258`):

- agent type must be selected,
- repo path is required,
- repo path is resolved with `Path.GetFullPath` and must exist as a directory,
  otherwise the dialog displays `Not a directory: <resolved>` and stays open.

`F2` (`OpenAgentDialog`, `MainWindow.cs:125-173`) lists every saved agent id
from `IAgentStateStore.ListAgentsAsync` and reopens the selected one in a new
tab.

### Live reasoning

The chat pane is the only place the model's "thinking" is visible. The render
logic lives at `AgentTabView.cs:198-246`:

- `ReasoningEvent` arrives during a turn → `SetLiveReasoning(text)` formats it
  with a `┄ thinking ┄` divider and a `▸ Header` line if the text begins with a
  `**bold**` lead-in (`AgentTabView.cs:291-323`). The formatted block is held
  in `_liveReasoning` and re-rendered on top of the committed transcript.
- A new `ReasoningEvent` **replaces** the previous live block (single-buffer)
  rather than appending — the user sees the model's current thought, not a
  scroll of every micro-thought.
- `FinalAnswerEvent`, `TurnCompletedEvent`, `TurnFailedEvent`, and
  `TurnSupersededEvent` all call `ClearReasoning()` so the live block goes
  away as soon as the turn settles (`AgentTabView.cs:227-243`).

Tool calls and turn-lifecycle events render to the **right-hand events pane**
instead, with glyphs: `⏵` turn start, `✦` model start, `🔧` tool call, `↳`
result, `⚠` failure, `⏹` end, `≡` summary (`AgentTabView.cs:198-246`).

### Markdown support in the chat pane

`MarkdownView` (`src/Archer.Tui/Ui/MarkdownView.cs:21`) renders a CommonMark
subset. Block-level constructs (`Reparse`, `MarkdownView.cs:152-237`):

| Construct          | Rendering                                              |
|--------------------|--------------------------------------------------------|
| `# / ## / ###`     | Bright-cyan / soft-blue / amber heading attribute      |
| `- ` / `* ` bullet | `•` glyph + indented inline-parsed text                |
| `> ` blockquote    | `▎` vertical-bar marker + inline-parsed text           |
| ```` ``` ```` fence| Contiguous block on warm raised background             |
| `--- / *** / ___`  | 80-column horizontal rule                              |
| empty line         | renders as a blank row                                 |

Inline (`ParseInline`, `MarkdownView.cs:239-290`):

| Construct      | Rendering                                                      |
|----------------|----------------------------------------------------------------|
| `` `code` ``   | Amber on a warm raised cell background                         |
| `**bold**`     | Bright gold accent                                             |
| `*italic*`     | Dim gray (single asterisk, not part of `**`)                   |

Styles are conveyed via `Terminal.Gui.Attribute` colors only — Terminal.Gui v2
exposes no bold/italic flags, so the palette in `Theme.MarkdownPalette`
(`Ui/Theme.cs:58-71`) substitutes color and background tint.

### iTerm2 mouse-reporting note

By default iTerm2 intercepts mouse clicks for its own selection/right-click
menus. To let Terminal.Gui see clicks (so `MarkdownView` mouse-wheel scrolling
and tab-clicking work), either:

- **Hold ⌥ Option while clicking** — iTerm forwards modified clicks to the
  application even when mouse reporting is otherwise off; or
- **Enable xterm mouse reporting in your iTerm profile**: Profiles → Terminal
  → check **Enable xterm mouse reporting**. Then unmodified clicks and the
  scroll wheel are delivered to the TUI directly.

The mouse-wheel handler lives at `MarkdownView.cs:49-62`; without forwarding it
will never fire.

### Logs and troubleshooting

- **Crash on startup with `archer-tui requires a real terminal`** — you're in
  a non-TTY runner. Use `scripts/tui-debug.sh` or run from a terminal.
- **Repo path doesn't exist** — pass `--repo` with an absolute path
  (`Program.cs:56-62`).
- **Mouse clicks ignored in iTerm** — see the iTerm note above.
- **TUI looks like blank panels** — check `<repo>/.archer/logs/tui-*.log`.
  Orleans messages and exceptions land there, never on the screen.

### See also

- [CLI.md](./CLI.md) — the same workflows from the command line
- [TELEMETRY.md](./TELEMETRY.md) — what events/spans the TUI emits while
  running
- [CONFIGURATION.md](./CONFIGURATION.md) — host configuration shared with the
  CLI
- ARCHITECTURE.md *(TODO — not yet written; will cover grains, event sink,
  state store)*
