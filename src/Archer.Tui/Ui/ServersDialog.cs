using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Client;
using Archer.Mcp.Tools;
using Archer.Mcp.Yaml;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui;
using TGApp = Terminal.Gui.Application;
using NoCoverage = System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute;

namespace Archer.Tui.Ui;

/// <summary>
/// Modal dialog for inspecting and managing MCP servers from the TUI. Mirrors the
/// <c>archer mcp</c> CLI subcommands: shows the live registry + credential status,
/// and offers Test / Login / Logout / Set-credentials actions on the selected row.
/// </summary>
public sealed class ServersDialog
{
    // UI-bound members are excluded from coverage at the method level: Terminal.Gui v2
    // can't initialize inside the xUnit test process. The pure logic — FormatRow,
    // FormatCredsMarker, FormatEndpoint, TryBuildCredentialsFromStrings — is unit-tested.

    private readonly IMcpServerRegistry _registry;
    private readonly ICredentialStore _credentials;
    private readonly IMcpClientPool _pool;
    private readonly McpToolSource? _source;

    [NoCoverage]
    public ServersDialog(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        _registry = sp.GetRequiredService<IMcpServerRegistry>();
        _credentials = sp.GetRequiredService<ICredentialStore>();
        _pool = sp.GetRequiredService<IMcpClientPool>();
        _source = sp.GetService<McpToolSource>();
    }

    [NoCoverage]
    public void Show()
    {
        var ui = BuildUi();
        ui.Refresh();
        TGApp.Run(ui.Dialog);
        ui.Dialog.Dispose();
    }

    // ---- view construction --------------------------------------------------------------

    [NoCoverage]
    private DialogUi BuildUi()
    {
        var dialog = new Dialog
        {
            Title = "MCP servers",
            Width = 90,
            Height = 22,
            ColorScheme = Theme.Frame,
        };
        var list = new ListView
        {
            X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(4),
            ColorScheme = Theme.List,
        };
        var status = new Label
        {
            X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Height = 1,
            Text = string.Empty, ColorScheme = Theme.Frame,
        };
        var rows = new System.Collections.ObjectModel.ObservableCollection<string>();
        list.SetSource<string>(rows);

        var servers = new List<(McpServerConfig Config, bool HasCreds)>();
        var ui = new DialogUi(dialog, list, status, rows, servers,
            reload: () => ReloadAsync(servers, rows),
            setStatus: msg => TGApp.Invoke(() => { status.Text = msg; }));

        AttachButtons(ui);
        dialog.Add(list, status);
        return ui;
    }

    [NoCoverage]
    private void AttachButtons(DialogUi ui)
    {
        AddButton(ui, "_Test", () => RunTestAsync(ui));
        AddButton(ui, "_Login (OAuth)", () => RunLoginAsync(ui));
        AddButton(ui, "Log_out", () => RunLogoutAsync(ui));
        AddButton(ui, "_Set creds", () => SetCredsHandler(ui));
        AddButton(ui, "_Refresh", () => { _ = ui.Refresh(); return Task.CompletedTask; });
        var close = new Button { Text = "_Close" };
        close.Accepting += (_, _) => TGApp.RequestStop(ui.Dialog);
        ui.Dialog.AddButton(close);
    }

    [NoCoverage]
    private static void AddButton(DialogUi ui, string text, Func<Task> handler)
    {
        var button = new Button { Text = text };
        button.Accepting += (_, e) =>
        {
            e.Cancel = true;  // keep the dialog open
            FireAndForget(handler);
        };
        ui.Dialog.AddButton(button);
    }

    /// <summary>Run an async task without awaiting it. Wraps the discard so static analysers
    /// don't flag every fire-and-forget UI handler as a "useless assignment".</summary>
    [NoCoverage]
    private static void FireAndForget(Func<Task> task) => _ = task();

    // ---- row formatting -----------------------------------------------------------------

    [NoCoverage]
    private async Task ReloadAsync(
        List<(McpServerConfig Config, bool HasCreds)> servers,
        System.Collections.ObjectModel.ObservableCollection<string> rows)
    {
        servers.Clear();
        foreach (var s in _registry.All.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var has = await _credentials.GetAsync(s.Name).ConfigureAwait(false) is not null;
            servers.Add((s, has));
        }
        TGApp.Invoke(() => RebuildRows(servers, rows));
    }

    [NoCoverage]
    private static void RebuildRows(
        List<(McpServerConfig Config, bool HasCreds)> servers,
        System.Collections.ObjectModel.ObservableCollection<string> rows)
    {
        rows.Clear();
        if (servers.Count == 0)
        {
            rows.Add(" (no MCP servers — drop a YAML in mcp/ or run `archer mcp add`)");
            return;
        }
        foreach (var (cfg, has) in servers)
        {
            rows.Add(FormatRow(cfg, has));
        }
    }

    internal static string FormatRow(McpServerConfig cfg, bool hasCreds)
    {
        var creds = FormatCredsMarker(cfg.Auth.Type, hasCreds);
        var transport = YamlMcpConfigLoader.TransportTypeToYaml(cfg.Transport.Type);
        var auth = YamlMcpConfigLoader.AuthTypeToYaml(cfg.Auth.Type);
        var endpoint = FormatEndpoint(cfg);
        return $" {cfg.Name,-18} {transport,-16} {auth,-9} {creds,-3} {endpoint}";
    }

    /// <summary>ASCII-only credential markers so column widths line up under any terminal font.</summary>
    internal static string FormatCredsMarker(McpAuthType authType, bool hasCreds)
    {
        if (authType == McpAuthType.None) return "-";
        return hasCreds ? "*" : "?";
    }

    internal static string FormatEndpoint(McpServerConfig cfg)
    {
        var endpoint = cfg.Transport.Endpoint?.ToString()
            ?? (cfg.Transport.Command is { Length: > 0 }
                ? $"{cfg.Transport.Command} {string.Join(' ', cfg.Transport.Args)}"
                : "(none)");
        return endpoint.Length > 38 ? endpoint[..35] + "..." : endpoint;
    }

    // ---- action handlers ----------------------------------------------------------------

    [NoCoverage]
    private async Task RunTestAsync(DialogUi ui)
    {
        if (ui.Selected() is not { } s) return;
        ui.SetStatus($"Connecting to '{s.Name}'…");
        try
        {
            var client = await _pool.GetAsync(s.Name).ConfigureAwait(false);
            var tools = await client.ListToolsAsync().ConfigureAwait(false);
            ui.SetStatus($"'{s.Name}': {tools.Count} tools available.");
        }
        catch (Exception ex)
        {
            ui.SetStatus($"'{s.Name}' connection failed: {ex.Message}");
        }
    }

    [NoCoverage]
    private async Task RunLoginAsync(DialogUi ui)
    {
        if (ui.Selected() is not { } s) return;
        if (s.Auth.Type != McpAuthType.OAuth)
        {
            ui.SetStatus($"'{s.Name}' is not an OAuth server.");
            return;
        }
        ui.SetStatus($"Opening browser for '{s.Name}'…");
        try
        {
            await _pool.EvictAsync(s.Name).ConfigureAwait(false);
            var client = await _pool.GetAsync(s.Name).ConfigureAwait(false);
            var tools = await client.ListToolsAsync().ConfigureAwait(false);
            if (_source is not null) await _source.RefreshAsync(s.Name).ConfigureAwait(false);
            ui.SetStatus($"Logged in to '{s.Name}'. {tools.Count} tools available.");
            await ui.Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Login failed: {ex.Message}");
        }
    }

    [NoCoverage]
    private async Task RunLogoutAsync(DialogUi ui)
    {
        if (ui.Selected() is not { } s) return;
        try
        {
            await _credentials.DeleteAsync(s.Name).ConfigureAwait(false);
            await _pool.EvictAsync(s.Name).ConfigureAwait(false);
            if (_source is not null) await _source.RefreshAsync(s.Name).ConfigureAwait(false);
            ui.SetStatus($"Logged out of '{s.Name}'.");
            await ui.Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ui.SetStatus($"Logout failed: {ex.Message}");
        }
    }

    [NoCoverage]
    private Task SetCredsHandler(DialogUi ui)
    {
        if (ui.Selected() is not { } s) return Task.CompletedTask;
        ShowCredentialsDialog(s);
        return ui.Refresh();
    }

    // ---- credentials sub-dialog ---------------------------------------------------------

    [NoCoverage]
    private void ShowCredentialsDialog(McpServerConfig server)
    {
        if (server.Auth.Type is McpAuthType.None or McpAuthType.OAuth)
        {
            // None doesn't need credentials; OAuth uses Login.
            return;
        }

        var dialog = new Dialog
        {
            Title = $"Credentials — {server.Name}",
            Width = 70, Height = 12,
            ColorScheme = Theme.Frame,
        };

        var (input1, input2) = BuildCredentialInputs(dialog, server.Auth.Type);

        var save = new Button { Text = "_Save", IsDefault = true };
        var cancel = new Button { Text = "_Cancel" };
        save.Accepting += (_, e) =>
        {
            if (TryBuildCredentials(server.Auth.Type, input1, input2, out var creds))
            {
                FireAndForget(() => SaveCredentialsAsync(server.Name, creds));
                TGApp.RequestStop(dialog);
            }
            else
            {
                e.Cancel = true;
            }
        };
        cancel.Accepting += (_, _) => TGApp.RequestStop(dialog);

        dialog.AddButton(save);
        dialog.AddButton(cancel);
        TGApp.Run(dialog);
        dialog.Dispose();
    }

    [NoCoverage]
    private static (TextField primary, TextField? secondary) BuildCredentialInputs(Dialog dialog, McpAuthType authType)
    {
        var label1 = new Label
        {
            X = 1, Y = 1, Width = Dim.Fill(2), Height = 1,
            Text = authType == McpAuthType.Bearer ? "Bearer token:" : "API key:",
        };
        var input1 = new TextField
        {
            X = 1, Y = 2, Width = Dim.Fill(2), Height = 1,
            ColorScheme = Theme.Input, Secret = true,
        };
        dialog.Add(label1, input1);

        if (authType != McpAuthType.ApiKey) return (input1, null);

        var label2 = new Label
        {
            X = 1, Y = 4, Width = Dim.Fill(2), Height = 1,
            Text = "Token:",
        };
        var input2 = new TextField
        {
            X = 1, Y = 5, Width = Dim.Fill(2), Height = 1,
            ColorScheme = Theme.Input, Secret = true,
        };
        dialog.Add(label2, input2);
        return (input1, input2);
    }

    [NoCoverage]
    private static bool TryBuildCredentials(
        McpAuthType authType, TextField primary, TextField? secondary, out ServerCredentials credentials)
    {
        var v1 = primary.Text?.ToString() ?? string.Empty;
        var v2 = secondary?.Text?.ToString() ?? string.Empty;
        return TryBuildCredentialsFromStrings(authType, v1, v2, out credentials);
    }

    /// <summary>
    /// Pure form-validation: returns true if the supplied raw strings (already extracted from
    /// the TextField inputs) compose a valid <see cref="ServerCredentials"/> for the given
    /// auth type. Extracted so the validation can be unit-tested without TextFields.
    /// </summary>
    internal static bool TryBuildCredentialsFromStrings(
        McpAuthType authType, string primary, string secondary, out ServerCredentials credentials)
    {
        credentials = new ServerCredentials();
        var v1 = primary.Trim();
        if (string.IsNullOrEmpty(v1)) return false;

        if (authType == McpAuthType.Bearer)
        {
            credentials = new ServerCredentials { BearerToken = v1 };
            return true;
        }
        var v2 = secondary.Trim();
        if (string.IsNullOrEmpty(v2)) return false;
        credentials = new ServerCredentials { ApiKey = new ApiKeyPair { Key = v1, Token = v2 } };
        return true;
    }

    [NoCoverage]
    private async Task SaveCredentialsAsync(string serverName, ServerCredentials credentials)
    {
        await _credentials.SaveAsync(serverName, credentials).ConfigureAwait(false);
        if (_source is not null) await _source.RefreshAsync(serverName).ConfigureAwait(false);
    }

    // ---- internal UI shape --------------------------------------------------------------

    [NoCoverage]
    private sealed class DialogUi
    {
        public Dialog Dialog { get; }
        public ListView List { get; }
        public Label Status { get; }
        public System.Collections.ObjectModel.ObservableCollection<string> Rows { get; }
        public List<(McpServerConfig Config, bool HasCreds)> Servers { get; }
        public Func<Task> Refresh { get; }
        public Action<string> SetStatus { get; }

        public DialogUi(
            Dialog dialog, ListView list, Label status,
            System.Collections.ObjectModel.ObservableCollection<string> rows,
            List<(McpServerConfig Config, bool HasCreds)> servers,
            Func<Task> reload,
            Action<string> setStatus)
        {
            Dialog = dialog;
            List = list;
            Status = status;
            Rows = rows;
            Servers = servers;
            Refresh = reload;
            SetStatus = setStatus;
        }

        public McpServerConfig? Selected()
        {
            var idx = List.SelectedItem;
            return idx >= 0 && idx < Servers.Count ? Servers[idx].Config : null;
        }
    }
}
