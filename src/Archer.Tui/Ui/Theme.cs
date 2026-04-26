using Terminal.Gui;

namespace Archer.Tui.Ui;

/// <summary>
/// Central color palette for Archer.Tui. All schemes share dark backgrounds; each pane gets
/// its own foreground accent so panels are distinguishable at a glance.
/// </summary>
internal static class Theme
{
    // Backgrounds — true black for body panels so foreground text reads at maximum contrast,
    // slightly raised colors for focused/input surfaces.
    private static readonly Color Bg0 = new(0, 0, 0);        // app/menu background
    private static readonly Color Bg1 = new(0, 0, 0);        // panel background (chat/events/todos body)
    private static readonly Color Bg2 = new(45, 55, 75);     // raised: focused row, input (clearly distinct)
    private static readonly Color BgStatus = new(8, 30, 18);

    // Frames — keep the user's preferred green borders, but a touch darker so they read on dark bg.
    private static readonly Color FrameGreen = new(80, 200, 120);

    // Foregrounds — pushed to max-bright on true black so iTerm's profile can't dim them.
    private static readonly Color ChatFg = new(255, 255, 255);     // pure white
    private static readonly Color UserFg = new(120, 200, 255);     // bright blue accent for "you>"
    private static readonly Color AgentFg = new(150, 255, 180);    // bright mint accent for "agent>"
    private static readonly Color TodosFg = new(255, 195, 100);    // bright amber
    private static readonly Color EventsFg = new(255, 255, 255);   // pure white for events
    private static readonly Color ReasoningFg = new(200, 220, 255); // bright lavender-blue
    private static readonly Color InputFg = new(255, 255, 255);
    private static readonly Color StatusFg = new(120, 255, 180);
    private static readonly Color MenuFg = new(255, 255, 255);
    private static readonly Color MenuHotFg = new(255, 215, 0);
    private static readonly Color FocusFg = new(255, 255, 255);

    // Focus background is kept identical to Normal background for read-only display surfaces
    // (chat/events/todos/frame). Without this, when the AgentTabView has focus, Terminal.Gui paints
    // the Focus attribute on its descendants and wipes the body to Bg2 (looks "gray" to the user).
    public static ColorScheme TopLevel { get; } = Build(MenuFg, Bg0, MenuHotFg, FrameGreen, FocusFg, Bg0);

    public static ColorScheme Frame { get; } = Build(FrameGreen, Bg1, FrameGreen, Bg1, FocusFg, Bg1);

    public static ColorScheme Chat { get; } = Build(ChatFg, Bg1, MenuHotFg, Bg1, ChatFg, Bg1);

    public static ColorScheme Todos { get; } = Build(TodosFg, Bg1, MenuHotFg, Bg1, TodosFg, Bg1);

    public static ColorScheme Events { get; } = Build(EventsFg, Bg1, ReasoningFg, Bg1, EventsFg, Bg1);

    // Input is the one surface that *should* highlight on focus.
    public static ColorScheme Input { get; } = Build(InputFg, Bg2, MenuHotFg, Bg2, FocusFg, Bg2);

    // ListView selection: Normal sits on the raised dark-blue background; Focus is a
    // brighter blue so the highlighted row pops out as you arrow through the list.
    private static readonly Color BgListFocus = new(70, 110, 170);
    public static ColorScheme List { get; } = Build(InputFg, Bg2, MenuHotFg, Bg2, new Color(255, 255, 255), BgListFocus);

    public static ColorScheme Status { get; } = Build(StatusFg, BgStatus, MenuHotFg, BgStatus, StatusFg, BgStatus);

    // Menu items in dropdowns: Focus background must differ from Normal background or the
    // arrow-key navigation has no visual highlight. Bg2 is the same raised colour we use for
    // input/focused-row surfaces.
    public static ColorScheme Menu { get; } = Build(MenuFg, Bg0, MenuHotFg, Bg0, FocusFg, Bg2);

    public static Color UserAccent => UserFg;
    public static Color AgentAccent => AgentFg;
    public static Color BorderColor => FrameGreen;

    public static MarkdownView.Palette MarkdownPalette { get; } = new()
    {
        Default = new Terminal.Gui.Attribute(ChatFg, Bg1),
        Bold = new Terminal.Gui.Attribute(new Color(255, 240, 180), Bg1),  // bright gold
        Italic = new Terminal.Gui.Attribute(new Color(170, 180, 200), Bg1), // dim
        Code = new Terminal.Gui.Attribute(new Color(255, 200, 120), new Color(35, 30, 20)), // amber on warm raised
        CodeBlock = new Terminal.Gui.Attribute(new Color(220, 220, 235), new Color(20, 25, 35)), // light on cool raised
        Heading1 = new Terminal.Gui.Attribute(new Color(120, 220, 255), Bg1), // bright cyan
        Heading2 = new Terminal.Gui.Attribute(new Color(180, 220, 255), Bg1), // softer blue
        Heading3 = new Terminal.Gui.Attribute(new Color(255, 220, 110), Bg1), // amber
        Bullet = new Terminal.Gui.Attribute(new Color(120, 220, 255), Bg1),  // cyan glyph
        Blockquote = new Terminal.Gui.Attribute(new Color(120, 220, 255), Bg1),
        Rule = new Terminal.Gui.Attribute(new Color(80, 100, 130), Bg1),
    };

    /// <summary>Apply our dark theme to Terminal.Gui's built-in color schemes too, so dialogs and
    /// menus look consistent.</summary>
    public static void ApplyGlobal()
    {
        Colors.ColorSchemes["TopLevel"] = TopLevel;
        Colors.ColorSchemes["Base"] = Frame;
        Colors.ColorSchemes["Dialog"] = Frame;
        Colors.ColorSchemes["Menu"] = Menu;
        Colors.ColorSchemes["Error"] = Build(new Color(255, 120, 120), Bg1, MenuHotFg, Bg1, FocusFg, Bg2);
    }

    private static ColorScheme Build(
        Color fg,
        Color bg,
        Color hotFg,
        Color hotBg,
        Color focusFg,
        Color focusBg) =>
        new()
        {
            Normal = new Terminal.Gui.Attribute(fg, bg),
            HotNormal = new Terminal.Gui.Attribute(hotFg, hotBg),
            Focus = new Terminal.Gui.Attribute(focusFg, focusBg),
            HotFocus = new Terminal.Gui.Attribute(hotFg, focusBg),
            Disabled = new Terminal.Gui.Attribute(new Color(90, 95, 105), bg),
        };
}
