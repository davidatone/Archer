using Archer.Tui.Ui;
using FluentAssertions;
using Terminal.Gui;

namespace Archer.Tui.Tests;

public class MarkdownViewTests
{
    private static MarkdownView.Palette MakePalette()
    {
        var fg = new Color(255, 255, 255);
        var bg = new Color(0, 0, 0);
        var attr = new Terminal.Gui.Attribute(fg, bg);
        return new MarkdownView.Palette
        {
            Default = attr, Bold = attr, Italic = attr, Code = attr, CodeBlock = attr,
            Heading1 = attr, Heading2 = attr, Heading3 = attr,
            Bullet = attr, Blockquote = attr, Rule = attr,
        };
    }

    [Fact]
    public void Construct_round_trips_text()
    {
        var view = new MarkdownView(MakePalette());
        view.Text = "Hello **bold** world";
        view.Text.Should().Be("Hello **bold** world");
    }

    [Fact]
    public void Setting_text_to_null_falls_back_to_empty_string()
    {
        var view = new MarkdownView(MakePalette());
        view.Text = null!;
        view.Text.Should().Be(string.Empty);
    }

    [Theory]
    [InlineData("# Heading 1")]
    [InlineData("## Heading 2")]
    [InlineData("### Heading 3")]
    [InlineData("- bullet item")]
    [InlineData("* asterisk bullet")]
    [InlineData("> blockquote")]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("```\ncode\n```")]
    [InlineData("Plain `inline` line")]
    [InlineData("Has **bold** and *italic*")]
    [InlineData("")]
    public void Various_markdown_lines_parse_without_throwing(string text)
    {
        var view = new MarkdownView(MakePalette());
        Action set = () => view.Text = text;
        set.Should().NotThrow();
    }

    [Fact]
    public void Multi_line_with_fenced_code_block_round_trips()
    {
        var view = new MarkdownView(MakePalette());
        var input = "intro\n```\nline1\nline2\n```\noutro";
        Action set = () => view.Text = input;
        set.Should().NotThrow();
        view.Text.Should().Be(input);
    }

    [Fact]
    public void ScrollToBottom_does_not_throw_when_no_driver()
    {
        var view = new MarkdownView(MakePalette());
        view.Text = "line1\nline2\nline3";
        Action act = () => view.ScrollToBottom();
        act.Should().NotThrow();
    }
}
