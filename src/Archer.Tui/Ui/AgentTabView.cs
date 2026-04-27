using Archer.Actors.Contracts;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Tui.Sessions;
using Terminal.Gui;
using TGApp = Terminal.Gui.Application;
using NoCoverage = System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute;

namespace Archer.Tui.Ui;

/// <summary>
/// One tab's worth of agent UI: chat (left), todos (top-right), event stream (bottom-right),
/// input box, and a status line at the bottom of the tab.
/// </summary>
public sealed class AgentTabView : View
{
    // Members directly bound to Terminal.Gui Views (the constructor's layout wiring,
    // input/event handlers, and methods that mutate _chat/_eventStream/_input/_status text)
    // are excluded from coverage at the method level: Terminal.Gui v2 cannot initialize
    // inside the xUnit test process. All the pure logic — event mapping, chat composition,
    // role labels, reasoning formatting, status-bar and task-list rendering — lives in
    // <see cref="EventRenderer"/> and <see cref="TextRenderer"/>, exercised by direct unit
    // tests.


    private readonly AgentSession _session;
    private readonly MarkdownView _chat;
    private readonly TextView _eventStream;
    private readonly TextField _input;
    private readonly Label _status;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _todoItems = [];
    private readonly System.Text.StringBuilder _committedChat = new();
    private readonly System.Text.StringBuilder _eventsBuffer = new();
    private string _liveReasoning = string.Empty;

    [NoCoverage]
    public AgentTabView(AgentSession session)
    {
        _session = session;
        Width = Dim.Fill();
        Height = Dim.Fill();
        BorderStyle = LineStyle.None;
        ColorScheme = Theme.Frame;
        // Without this, focus can't traverse into our children; the input never receives keystrokes.
        CanFocus = true;

        // Reserve 4 rows at the bottom: prompt-frame (3 with border) + status (1).
        var chatFrame = new FrameView
        {
            Title = "Chat",
            X = 0, Y = 0,
            Width = Dim.Percent(60),
            Height = Dim.Fill(4),
            BorderStyle = LineStyle.Rounded,
            ColorScheme = Theme.Frame,
        };
        _chat = new MarkdownView(Theme.MarkdownPalette)
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Theme.Chat,
        };
        chatFrame.Add(_chat);

        var todosFrame = new FrameView
        {
            Title = "Todos",
            X = Pos.Right(chatFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(40),
            BorderStyle = LineStyle.Rounded,
            ColorScheme = Theme.Frame,
        };
        var todos = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Theme.Todos,
        };
        todos.SetSource<string>(_todoItems);
        todosFrame.Add(todos);

        var eventsFrame = new FrameView
        {
            Title = "Events / reasoning",
            X = Pos.Right(chatFrame),
            Y = Pos.Bottom(todosFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            BorderStyle = LineStyle.Rounded,
            ColorScheme = Theme.Frame,
        };
        _eventStream = new TextView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true,
            ColorScheme = Theme.Events,
        };
        eventsFrame.Add(_eventStream);

        // Place the TextField directly on the tab view (no FrameView wrapper) — wrappers in
        // Terminal.Gui v2 swallow mouse events and break the focus chain. Use a Label above
        // the input for the "Prompt" title and the input's own border for visibility.
        var promptLabel = new Label
        {
            Text = "Prompt — Enter to send · PgUp/PgDn (or Ctrl+Home/End) scroll the chat",
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Theme.Frame,
        };
        _input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Theme.Input,
            CanFocus = true,
            BorderStyle = LineStyle.None,
        };
        _input.KeyDown += OnInputKey;

        _status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Theme.Status,
        };

        Add(chatFrame, todosFrame, eventsFrame, promptLabel, _input, _status);

        // Focus the input as soon as the view is fully initialized — earlier calls to SetFocus
        // get swallowed because layout hasn't happened yet, which leaves keystrokes flowing to
        // the menu bar's hotkey handler.
        Initialized += (_, _) => _input.SetFocus();

        _session.EventReceived += OnEventReceived;
        _session.StateChanged += OnStateChanged;
        UpdateStatus();
        AppendChat($"Connected to {session.AgentId} — repo: {session.RepoRoot}\n");
        _ = _session.RefreshSnapshotAsync();
    }

    [NoCoverage]
    public void FocusInput()
    {
        if (IsInitialized)
        {
            _input.SetFocus();
        }
    }

    [NoCoverage]
    private void OnInputKey(object? sender, Key e)
    {
        if (e.KeyCode == KeyCode.Enter)
        {
            var text = _input.Text?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                e.Handled = true;
                return;
            }
            _input.Text = string.Empty;
            e.Handled = true;
            _ = SendUserMessageAsync(text.Trim());
            return;
        }

        // Forward chat-pane scroll keys without stealing focus. The input is single-line so
        // these have no native meaning here; forwarding them lets users page through long
        // responses (e.g. a streamed code block) without leaving the prompt.
        if (TryForwardScrollKey(e))
        {
            e.Handled = true;
        }
    }

    [NoCoverage]
    private bool TryForwardScrollKey(Key e)
    {
        switch (e.KeyCode)
        {
            case KeyCode.PageUp:
                _chat.ScrollByPages(-1);
                return true;
            case KeyCode.PageDown:
                _chat.ScrollByPages(+1);
                return true;
            case KeyCode.Home when e.IsCtrl:
                _chat.ScrollToTop();
                return true;
            case KeyCode.End when e.IsCtrl:
                _chat.ScrollToBottom();
                return true;
        }
        return false;
    }

    [NoCoverage]
    private async Task SendUserMessageAsync(string text)
    {
        AppendChat($"\nyou> {text}\n");
        try
        {
            // Snapshot tells us truthfully whether this agent has been initialized
            // (returns null when uninitialized, populated otherwise).
            var snap = await _session.Grain.GetSnapshotAsync();
            if (snap is null)
            {
                await _session.Grain.InitializeAsync(new NewAgentRequest(_session.RepoRoot, text, AgentDefinitionId: _session.AgentType));
            }
            else
            {
                await _session.Grain.AddUserMessageAsync(new UserMessageInput(text));
            }
        }
        catch (Exception ex)
        {
            AppendChat($"\n[error] {ex.Message}\n");
        }
    }

    [NoCoverage]
    private void OnEventReceived(AgentEvent evt) =>
        TGApp.Invoke(() => RenderEvent(evt));

    [NoCoverage]
    private void OnStateChanged() =>
        TGApp.Invoke(() =>
        {
            UpdateTodos();
            UpdateChatFromMessages();
            UpdateStatus();
        });

    [NoCoverage]
    private void RenderEvent(AgentEvent evt)
    {
        var instr = EventRenderer.Render(evt);
        if (instr.AppendToChat is not null) AppendChat(instr.AppendToChat);
        if (instr.AppendToEvents is not null) AppendEvents(instr.AppendToEvents);
        switch (instr.Reasoning)
        {
            case EventRenderer.ReasoningOp.Set when instr.SetReasoning is not null:
                SetLiveReasoning(instr.SetReasoning);
                break;
            case EventRenderer.ReasoningOp.Clear:
                ClearReasoning();
                break;
        }
        UpdateStatus();
    }

    [NoCoverage]
    private void UpdateChatFromMessages()
    {
        _committedChat.Clear();
        _committedChat.Append(TextRenderer.FormatTranscript(_session.Messages));
        RenderChat();
    }

    [NoCoverage]
    private void SetLiveReasoning(string text)
    {
        _liveReasoning = TextRenderer.FormatReasoning(text);
        RenderChat();
    }

    [NoCoverage]
    private void ClearReasoning()
    {
        if (_liveReasoning.Length == 0) return;
        _liveReasoning = string.Empty;
        RenderChat();
    }

    [NoCoverage]
    private void RenderChat()
    {
        _chat.Text = TextRenderer.ComposeChat(_committedChat.ToString(), _liveReasoning);
        _chat.ScrollToBottom();
    }

    [NoCoverage]
    private void UpdateTodos()
    {
        _todoItems.Clear();
        foreach (var t in _session.Todos)
        {
            _todoItems.Add(TextRenderer.FormatTodo(t));
        }
    }

    [NoCoverage]
    private void UpdateStatus()
    {
        _status.Text = TextRenderer.FormatStatus(
            _session.AgentId,
            _session.ActiveTurnId,
            _session.IsThinking,
            _session.Messages.Count,
            _session.Todos.Count);
    }

    [NoCoverage]
    private void AppendChat(string text)
    {
        _committedChat.Append(text);
        RenderChat();
    }

    [NoCoverage]
    private void AppendEvents(string line)
    {
        _eventsBuffer.AppendLine(line);
        _eventStream.Text = _eventsBuffer.ToString();
        _eventStream.MoveEnd();
    }


    [NoCoverage]
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.EventReceived -= OnEventReceived;
            _session.StateChanged -= OnStateChanged;
        }
        base.Dispose(disposing);
    }
}
