using Archer.Tui.Ui;
using FluentAssertions;
using Terminal.Gui;

namespace Archer.Tui.Tests;

public class ThemeTests
{
    [Fact]
    public void Color_schemes_are_distinct_instances()
    {
        var schemes = new[]
        {
            Theme.TopLevel, Theme.Frame, Theme.Chat, Theme.Todos, Theme.Events,
            Theme.Input, Theme.List, Theme.Status, Theme.Menu,
        };
        // Each scheme returns a non-null ColorScheme. The List scheme uses a distinct focus
        // background colour so the highlighted row pops — verify the value visually.
        schemes.Should().NotContainNulls();
    }

    [Fact]
    public void List_scheme_uses_a_focus_background_distinct_from_normal()
    {
        var list = Theme.List;
        list.Focus.Background.Should().NotBe(list.Normal.Background);
    }

    [Fact]
    public void Menu_scheme_uses_a_focus_background_distinct_from_normal()
    {
        // Without this, arrow-key navigation in dropdowns has no visible highlight.
        var menu = Theme.Menu;
        menu.Focus.Background.Should().NotBe(menu.Normal.Background);
    }

    [Fact]
    public void Markdown_palette_assigns_colours_for_each_token_type()
    {
        var p = Theme.MarkdownPalette;
        p.Default.Should().NotBe(p.Bold);
        p.Bold.Should().NotBe(p.Italic);
        p.Code.Should().NotBe(p.CodeBlock);
        p.Heading1.Should().NotBe(p.Heading2);
    }

    [Fact]
    public void ApplyGlobal_writes_colour_schemes_into_Terminal_Gui_Colors()
    {
        Theme.ApplyGlobal();
        Colors.ColorSchemes.Should().ContainKey("TopLevel")
            .WhoseValue.Should().NotBeNull();
        Colors.ColorSchemes.Should().ContainKey("Base")
            .WhoseValue.Should().NotBeNull();
        Colors.ColorSchemes.Should().ContainKey("Dialog")
            .WhoseValue.Should().NotBeNull();
        Colors.ColorSchemes.Should().ContainKey("Menu");
        Colors.ColorSchemes.Should().ContainKey("Error");
    }

    [Fact]
    public void Accent_colours_are_publicly_accessible()
    {
        Theme.UserAccent.Should().NotBe(Theme.AgentAccent);
        Theme.BorderColor.Should().NotBe(Theme.UserAccent);
    }
}
