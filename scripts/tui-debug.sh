#!/usr/bin/env bash
# Launches Archer.Tui in a new external terminal window so Terminal.Gui has a real PTY.
# On macOS, Rider's "useExternalConsole" is ignored — use this script + Run → Attach to Process.
#
# Prefers iTerm2 if installed, falls back to Terminal.app. Override with --terminal=iterm|terminal.
#
# Usage:
#   scripts/tui-debug.sh                       # default repo (current dir), auto-pick terminal
#   scripts/tui-debug.sh --repo PATH           # launch against a specific repo
#   scripts/tui-debug.sh --wait                # pause 10s before launching so you can attach
#   scripts/tui-debug.sh --terminal=iterm      # force iTerm2
#   scripts/tui-debug.sh --terminal=terminal   # force Terminal.app
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Archer.Tui/Archer.Tui.csproj"
WAIT_FOR_DEBUGGER=0
TUI_REPO="$REPO_ROOT"
TERMINAL_PREFERENCE=""
ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --wait) WAIT_FOR_DEBUGGER=1; shift ;;
    --repo) TUI_REPO="$2"; shift 2 ;;
    --terminal=*) TERMINAL_PREFERENCE="${1#--terminal=}"; shift ;;
    --terminal) TERMINAL_PREFERENCE="$2"; shift 2 ;;
    *)      ARGS+=("$1"); shift ;;
  esac
done

echo "Killing any existing archer-tui processes..."
pkill -9 -f archer-tui 2>/dev/null || true
pkill -9 -f "src/Archer.Tui/bin" 2>/dev/null || true
sleep 0.3

echo "Building $PROJECT..."
dotnet build "$PROJECT" -nologo -v:q

DLL="$REPO_ROOT/src/Archer.Tui/bin/Debug/net10.0/archer-tui.dll"
if [[ ! -f "$DLL" ]]; then
  echo "Build output not found at $DLL" >&2
  exit 1
fi

# Resolve --repo to an absolute path so the new terminal tab's CWD doesn't matter.
# (iTerm tabs may default to $HOME depending on profile.)
if [[ -d "$TUI_REPO" ]]; then
  TUI_REPO="$(cd "$TUI_REPO" && pwd)"
elif [[ -d "$REPO_ROOT/$TUI_REPO" ]]; then
  TUI_REPO="$(cd "$REPO_ROOT/$TUI_REPO" && pwd)"
else
  echo "Repo path not found: $TUI_REPO (relative to $(pwd) or $REPO_ROOT)" >&2
  exit 1
fi
echo "Repo: $TUI_REPO"

# Choose terminal app.
choose_terminal() {
  case "${TERMINAL_PREFERENCE:-}" in
    iterm)    echo "iterm"; return ;;
    terminal) echo "terminal"; return ;;
    "")       ;; # auto-detect
    *)        echo "Unknown --terminal value: $TERMINAL_PREFERENCE (use iterm or terminal)" >&2; exit 2 ;;
  esac
  if [[ -d /Applications/iTerm.app ]]; then echo "iterm"; else echo "terminal"; fi
}
TERMINAL_APP="$(choose_terminal)"

# Build the command line that runs inside the new window.
COMMAND="cd '$REPO_ROOT' && export DOTNET_ENVIRONMENT=Development && export ASPNETCORE_ENVIRONMENT=Development"
if [[ $WAIT_FOR_DEBUGGER -eq 1 ]]; then
  COMMAND+=" && echo \"PID: \$\$\" && echo 'Attach Rider to this process now (Run → Attach to Process → archer-tui), then press Enter.' && read"
fi
COMMAND+=" && exec dotnet '$DLL' --repo '$TUI_REPO' ${ARGS[*]:-}"

if [[ "$TERMINAL_APP" == "iterm" ]]; then
  echo "Launching in iTerm2..."
  osascript <<APPLESCRIPT
tell application "iTerm"
    activate
    if (count of windows) = 0 then
        create window with default profile
    else
        tell current window to create tab with default profile
    end if
    tell current session of current window
        write text "$COMMAND"
    end tell
end tell
APPLESCRIPT
else
  echo "Launching in Terminal.app..."
  osascript <<APPLESCRIPT
tell application "Terminal"
    activate
    do script "$COMMAND"
end tell
APPLESCRIPT
fi

echo "Done. Use Rider → Run → Attach to Process → archer-tui to debug."
