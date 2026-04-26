using Archer.Application.Agents;
using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Tui.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Terminal.Gui;
using TGApp = Terminal.Gui.Application;

namespace Archer.Tui.Ui;

/// <summary>
/// Top-level Terminal.Gui window. The pure logic (default agent picking, repo path
/// validation, tab-label formatting) lives in <see cref="MainWindowHelpers"/> and is unit
/// tested separately. The methods on this class are layout/menu wiring that requires an
/// initialized <c>Terminal.Gui.Application</c> driver to exercise — Terminal.Gui v2 cannot
/// boot inside the xUnit test process (TypeLoadException for
/// <c>System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute</c> via
/// <c>Microsoft.TestPlatform.CoreUtilities</c>), so this class is excluded from coverage.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class MainWindow : Toplevel
{
    private readonly IServiceProvider _sp;
    private readonly IGrainFactory _grainFactory;
    private readonly IAgentEventSink _sink;
    private readonly IAgentStateStore _store;
    private readonly IAgentBlobStore _blobStore;
    private readonly IAgentDefinitionRegistry _definitions;
    private readonly TabView _tabs;
    private readonly Dictionary<Tab, AgentSession> _sessions = [];
    private readonly string _defaultRepo;

    public MainWindow(IServiceProvider sp, string defaultRepo)
    {
        _sp = sp;
        _defaultRepo = defaultRepo;
        _grainFactory = sp.GetRequiredService<IGrainFactory>();
        _sink = sp.GetRequiredService<IAgentEventSink>();
        _store = sp.GetRequiredService<IAgentStateStore>();
        _blobStore = sp.GetRequiredService<IAgentBlobStore>();
        _definitions = sp.GetRequiredService<IAgentDefinitionRegistry>();

        Theme.ApplyGlobal();
        ColorScheme = Theme.TopLevel;

        var menu = new MenuBar
        {
            Menus =
            [
                new MenuBarItem("_File",
                [
                    new MenuItem("_New agent…", "Pick agent type and repo", PromptForRepoAndOpen),
                    new MenuItem("_Open existing…", "Resume agent by id", OpenAgentDialog),
                    null!,
                    new MenuItem("_Quit", "Exit", () => TGApp.RequestStop(this)),
                ]),
                new MenuBarItem("_Agent",
                [
                    new MenuItem("_Interrupt", "Supersede the active turn",
                        InterruptCurrent, () => CurrentSession() is not null),
                    new MenuItem("_Refresh", "Reload state from store",
                        () => _ = (CurrentSession()?.RefreshSnapshotAsync() ?? Task.CompletedTask),
                        () => CurrentSession() is not null),
                ]),
                new MenuBarItem("_Servers",
                [
                    new MenuItem("_Manage MCP servers…", "Test, login, logout, set credentials",
                        () => new ServersDialog(_sp).Show()),
                ]),
            ],
        };

        _tabs = new TabView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _tabs.SelectedTabChanged += (_, args) =>
        {
            if (args.NewTab?.View is AgentTabView view)
            {
                TGApp.Invoke(view.FocusInput);
            }
        };

        var statusBar = new StatusBar(
        [
            new(KeyCode.F1, "~F1~ New agent", PromptForRepoAndOpen),
            new(KeyCode.F2, "~F2~ Open", OpenAgentDialog),
            new(KeyCode.F3, "~F3~ Interrupt", InterruptCurrent),
            new(KeyCode.F10 | KeyCode.AltMask, "~Alt-F10~ Quit", () => TGApp.RequestStop(this)),
        ]);

        Add(menu, _tabs, statusBar);

        OpenNewAgentTab();
    }

    private AgentSession? CurrentSession()
    {
        var tab = _tabs.SelectedTab;
        if (tab is null)
        {
            return null;
        }
        return _sessions.TryGetValue(tab, out var s) ? s : null;
    }

    private void OpenNewAgentTab(
        string? agentId = null,
        string? repoOverride = null,
        string? agentType = null)
    {
        var id = agentId ?? AgentId.New();
        var repo = repoOverride ?? _defaultRepo;
        // Default to the first registered agent definition (alphabetical) so the startup
        // tab works in any repo, not just one that happens to ship a 'code-scout' YAML.
        agentType ??= MainWindowHelpers.PickDefaultAgentType(_definitions.All.Select(d => d.Id));
        var session = new AgentSession(id, repo, agentType, _grainFactory, _sink, _blobStore, dispatch: TGApp.Invoke);
        _sessions[CreateTabFor(session)] = session;
    }

    private Tab CreateTabFor(AgentSession session)
    {
        var view = new AgentTabView(session);
        var label = MainWindowHelpers.FormatTabLabel(session.AgentId);
        var tab = new Tab
        {
            DisplayText = label,
            View = view,
        };
        _tabs.AddTab(tab, andSelect: true);
        // Defer focus so the tab view has been laid out before we ask its input to take focus.
        TGApp.Invoke(view.FocusInput);
        return tab;
    }

    private void OpenAgentDialog() => _ = OpenAgentDialogAsync();

    private async Task OpenAgentDialogAsync()
    {
        var ids = await _store.ListAgentsAsync().ConfigureAwait(false);
        if (ids.Count == 0)
        {
            TGApp.Invoke(() => MessageBox.Query("Open agent", "No saved agents found.", "OK"));
            return;
        }
        TGApp.Invoke(() => ShowOpenAgentDialog(ids));
    }

    private void ShowOpenAgentDialog(IReadOnlyList<string> ids)
    {

        var picker = new ListView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        var idList = new System.Collections.ObjectModel.ObservableCollection<string>(ids);
        picker.SetSource<string>(idList);

        var dialog = new Dialog
        {
            Title = "Open existing agent",
            Width = 50,
            Height = 12,
        };
        var ok = new Button { Text = "Open", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        ok.Accepting += (_, _) =>
        {
            var idx = picker.SelectedItem;
            if (idx >= 0 && idx < idList.Count)
            {
                OpenNewAgentTab(idList[idx]);
            }
            TGApp.RequestStop(dialog);
        };
        cancel.Accepting += (_, _) => TGApp.RequestStop(dialog);

        dialog.Add(picker);
        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        TGApp.Run(dialog);
        dialog.Dispose();
    }

    private void PromptForRepoAndOpen()
    {
        var ids = _definitions.All.Select(d => d.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            MessageBox.ErrorQuery("No agents", "No agent definitions registered. Drop a YAML in agents/.", "OK");
            return;
        }

        var dialog = new Dialog
        {
            Title = "New agent",
            Width = 70,
            Height = 16,
        };

        var typeLabel = new Label
        {
            X = 1, Y = 0,
            Text = "Agent type:",
            Width = Dim.Fill(2), Height = 1,
        };
        // RadioGroup gives the standard "pick one" interaction — arrow keys move the
        // highlight, spacebar/Enter selects and toggles the (•) marker.
        var typePicker = new RadioGroup
        {
            X = 1, Y = 1,
            Width = Dim.Fill(2),
            Height = ids.Count,
            RadioLabels = ids.ToArray(),
            SelectedItem = 0,
            ColorScheme = Theme.Input,
        };

        var pathLabel = new Label
        {
            X = 1, Y = 6,
            Text = "Repository path (absolute or relative to current working directory):",
            Width = Dim.Fill(2), Height = 1,
        };
        var pathInput = new TextField
        {
            X = 1, Y = 8,
            Width = Dim.Fill(2),
            Height = 1,
            Text = _defaultRepo,
            ColorScheme = Theme.Input,
        };

        var ok = new Button { Text = "Open", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        var error = new Label
        {
            X = 1, Y = 10,
            Width = Dim.Fill(2),
            Height = 1,
            ColorScheme = Theme.Frame,
        };

        ok.Accepting += (_, e) =>
        {
            var idx = typePicker.SelectedItem;
            if (idx < 0 || idx >= ids.Count)
            {
                error.Text = "Pick an agent type.";
                e.Cancel = true;
                return;
            }
            if (!MainWindowHelpers.TryResolveRepoPath(pathInput.Text?.ToString(), out var resolved, out var msg))
            {
                error.Text = msg;
                e.Cancel = true;
                return;
            }
            OpenNewAgentTab(repoOverride: resolved, agentType: ids[idx]);
            TGApp.RequestStop(dialog);
        };
        cancel.Accepting += (_, _) => TGApp.RequestStop(dialog);

        dialog.Add(typeLabel, typePicker, pathLabel, pathInput, error);
        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        TGApp.Invoke(() => typePicker.SetFocus());
        TGApp.Run(dialog);
        dialog.Dispose();
    }

    private void InterruptCurrent()
    {
        var session = CurrentSession();
        if (session is null)
        {
            return;
        }
        _ = session.Grain.InterruptAsync(new InterruptRequest("User pressed Interrupt."));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var s in _sessions.Values)
            {
                s.Dispose();
            }
            _sessions.Clear();
        }
        base.Dispose(disposing);
    }
}
