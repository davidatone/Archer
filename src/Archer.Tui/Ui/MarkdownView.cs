using System.Drawing;
using Terminal.Gui;
using TGApp = Terminal.Gui.Application;
using TGAttribute = Terminal.Gui.Attribute;
using NoCoverage = System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute;

namespace Archer.Tui.Ui;

/// <summary>
/// A read-only View that renders a subset of CommonMark with attribute-level styling.
/// Terminal.Gui v2 doesn't expose bold/italic flags on Attribute, so styles are conveyed by
/// color and background tint:
///   - **bold**       → bright accent
///   - *italic*       → dim
///   - `code`         → amber on raised bg
///   - # / ## / ###   → bright color, bigger separator above
///   - - / * bullets  → glyph + indented text
///   - > blockquote   → vertical bar marker
///   - ```fence```    → contiguous block on raised bg
/// Auto-scrolls to the bottom when Text is set.
/// </summary>
public sealed class MarkdownView : View
{
    public sealed class Palette
    {
        public required TGAttribute Default { get; init; }
        public required TGAttribute Bold { get; init; }
        public required TGAttribute Italic { get; init; }
        public required TGAttribute Code { get; init; }
        public required TGAttribute CodeBlock { get; init; }
        public required TGAttribute Heading1 { get; init; }
        public required TGAttribute Heading2 { get; init; }
        public required TGAttribute Heading3 { get; init; }
        public required TGAttribute Bullet { get; init; }
        public required TGAttribute Blockquote { get; init; }
        public required TGAttribute Rule { get; init; }
    }

    private readonly Palette _palette;
    private readonly List<List<Span>> _lines = [];
    private readonly List<List<Span>> _wrapped = [];
    private int _wrappedAtWidth = -1;
    private string _text = string.Empty;

    public MarkdownView(Palette palette)
    {
        _palette = palette;
        CanFocus = true;     // Allow Tab-to-focus + arrow/page-up keys for scrolling.
        WantMousePositionReports = true;
    }

    [NoCoverage]
    protected override bool OnMouseEvent(MouseEventArgs mouseEvent)
    {
        if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            ScrollBy(-3);
            return true;
        }
        if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            ScrollBy(+3);
            return true;
        }
        return base.OnMouseEvent(mouseEvent);
    }

    [NoCoverage]
    protected override bool OnKeyDown(Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp: ScrollBy(-1); return true;
            case KeyCode.CursorDown: ScrollBy(+1); return true;
            case KeyCode.PageUp: ScrollBy(-Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.PageDown: ScrollBy(+Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.Home: SetViewportY(0); return true;
            case KeyCode.End: ScrollToBottom(); return true;
        }
        return base.OnKeyDown(key);
    }

    [NoCoverage]
    private void ScrollBy(int delta) => SetViewportY(Viewport.Y + delta);

    [NoCoverage]
    private void SetViewportY(int y)
    {
        var totalLines = WrappedLines(Viewport.Width).Count;
        var maxY = Math.Max(0, totalLines - Viewport.Height);
        var clamped = Math.Clamp(y, 0, maxY);
        if (clamped == Viewport.Y) return;
        Viewport = new Rectangle(0, clamped, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
    }

    public new string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            Reparse();
            SetNeedsDraw();
        }
    }

    public void ScrollToBottom()
    {
        // Reset viewport to show the last `Viewport.Height` lines.
        var contentHeight = WrappedLines(Viewport.Width).Count;
        var visible = Viewport.Height;
        if (contentHeight > visible)
        {
            Viewport = new Rectangle(0, contentHeight - visible, Viewport.Width, Viewport.Height);
        }
        SetNeedsDraw();
    }

    [NoCoverage]
    protected override bool OnDrawingContent()
    {
        var driver = TGApp.Driver;
        if (driver is null) return false;

        var visibleRows = Viewport.Height;
        var top = Viewport.Y;
        var width = Viewport.Width;
        var wrapped = WrappedLines(width);

        for (var rowOnScreen = 0; rowOnScreen < visibleRows; rowOnScreen++)
        {
            DrawRow(driver, rowOnScreen, top + rowOnScreen, width, wrapped);
        }
        return true;
    }

    [NoCoverage]
    private void DrawRow(Terminal.Gui.IConsoleDriver driver, int rowOnScreen, int lineIndex, int width, List<List<Span>> wrapped)
    {
        // Clear the row first so trailing background of a shorter wrapped line doesn't bleed.
        Move(0, rowOnScreen);
        driver.SetAttribute(_palette.Default);
        for (var i = 0; i < width; i++) driver.AddRune((System.Text.Rune)' ');

        if (lineIndex < 0 || lineIndex >= wrapped.Count) return;

        Move(0, rowOnScreen);
        var col = 0;
        foreach (var span in wrapped[lineIndex])
        {
            if (col >= width) break;
            col += DrawSpan(driver, span, col, width);
        }
    }

    [NoCoverage]
    private static int DrawSpan(Terminal.Gui.IConsoleDriver driver, Span span, int col, int width)
    {
        driver.SetAttribute(span.Style);
        var painted = 0;
        foreach (var rune in span.Text.EnumerateRunes())
        {
            if (col + painted >= width) break;
            driver.AddRune(rune);
            // Emoji and other supplementary-plane characters typically render two columns wide.
            painted += rune.Value > 0xFFFF ? 2 : 1;
        }
        return painted;
    }

    /// <summary>
    /// Lazily compute (and cache) word-wrapped visual lines for the current viewport width.
    /// Reparse invalidates the cache; resize re-wraps on the next draw.
    /// </summary>
    private List<List<Span>> WrappedLines(int width)
    {
        if (width <= 0)
        {
            return _lines;
        }
        if (_wrappedAtWidth == width)
        {
            return _wrapped;
        }
        _wrapped.Clear();
        foreach (var logical in _lines)
        {
            WrapLogicalLine(logical, width, _wrapped);
        }
        _wrappedAtWidth = width;
        return _wrapped;
    }

    /// <summary>
    /// Word-wrap one logical line into one or more visual lines, preserving span styles
    /// across wrap boundaries. Wraps at the last space within the window when possible;
    /// hard-breaks for words longer than the window.
    /// </summary>
    private static void WrapLogicalLine(IReadOnlyList<Span> logical, int width, List<List<Span>> output)
    {
        var flat = FlattenSpans(logical);
        if (flat.Count == 0)
        {
            output.Add([]);
            return;
        }

        var i = 0;
        while (i < flat.Count)
        {
            var lineEnd = ComputeLineEnd(flat, i, width);
            output.Add(BuildVisualLine(flat, i, lineEnd));

            // Skip a wrap-point space so the next line doesn't start with leading whitespace.
            i = (lineEnd < flat.Count && flat[lineEnd].Rune == ' ') ? lineEnd + 1 : lineEnd;
        }
    }

    /// <summary>Flatten spans into (rune, style) so wrapping doesn't have to slice strings.</summary>
    private static List<(int Rune, TGAttribute Style)> FlattenSpans(IReadOnlyList<Span> logical)
    {
        var flat = new List<(int Rune, TGAttribute Style)>();
        foreach (var span in logical)
        {
            foreach (var rune in span.Text.EnumerateRunes())
            {
                flat.Add((rune.Value, span.Style));
            }
        }
        return flat;
    }

    /// <summary>Hard line-end is `i + width`; back up to the last space if we'd cut mid-word.</summary>
    private static int ComputeLineEnd(List<(int Rune, TGAttribute Style)> flat, int start, int width)
    {
        var lineEnd = Math.Min(start + width, flat.Count);
        if (lineEnd >= flat.Count) return lineEnd;
        for (var j = lineEnd - 1; j > start; j--)
        {
            if (flat[j].Rune == ' ') return j;
        }
        return lineEnd;
    }

    /// <summary>Coalesce consecutive same-style runes back into spans for the visual row.</summary>
    private static List<Span> BuildVisualLine(List<(int Rune, TGAttribute Style)> flat, int start, int end)
    {
        var visual = new List<Span>();
        var k = start;
        while (k < end)
        {
            var style = flat[k].Style;
            var sb = new System.Text.StringBuilder();
            while (k < end && flat[k].Style.Equals(style))
            {
                sb.Append(char.ConvertFromUtf32(flat[k].Rune));
                k++;
            }
            visual.Add(new Span(sb.ToString(), style));
        }
        return visual;
    }

    private sealed record Span(string Text, TGAttribute Style);

    private void Reparse()
    {
        _lines.Clear();
        _wrappedAtWidth = -1;  // invalidate the wrap cache; next draw re-wraps
        var raw = _text.Replace("\r\n", "\n");
        var inFence = false;
        foreach (var rawLine in raw.Split('\n'))
        {
            _lines.Add(ParseLine(rawLine, ref inFence));
        }
    }

    /// <summary>Convert one source line to its visual span list. Handles fenced code blocks
    /// (toggles <paramref name="inFence"/>), headings, bullets, blockquotes, rules, and plain
    /// paragraphs. Each branch is small enough to read straight through.</summary>
    private List<Span> ParseLine(string line, ref bool inFence)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
        {
            inFence = !inFence;
            return [new Span(line, _palette.Rule)];
        }
        if (inFence)
        {
            return [new Span(" " + line, _palette.CodeBlock)];
        }
        if (string.IsNullOrEmpty(line))
        {
            return [];
        }
        if (TryParseHeading(line) is { } heading)
        {
            return heading;
        }

        var trimmed = line.TrimStart();
        var indent = line.Length - trimmed.Length;
        if (TryParsePrefixed(trimmed, indent) is { } prefixed)
        {
            return prefixed;
        }
        if (trimmed is "---" or "***" or "___")
        {
            return [new Span(new string('─', 80), _palette.Rule)];
        }
        return ParseInline(line);
    }

    private List<Span>? TryParseHeading(string line)
    {
        if (line.StartsWith("### ", StringComparison.Ordinal)) return [new Span(line[4..], _palette.Heading3)];
        if (line.StartsWith("## ", StringComparison.Ordinal)) return [new Span(line[3..], _palette.Heading2)];
        if (line.StartsWith("# ", StringComparison.Ordinal)) return [new Span(line[2..], _palette.Heading1)];
        return null;
    }

    private List<Span>? TryParsePrefixed(string trimmed, int indent)
    {
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return BuildPrefixedLine(trimmed[2..], indent, "• ", _palette.Bullet);
        }
        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            return BuildPrefixedLine(trimmed[2..], indent, "▎ ", _palette.Blockquote);
        }
        return null;
    }

    private List<Span> BuildPrefixedLine(string content, int indent, string glyph, TGAttribute glyphStyle)
    {
        var spans = new List<Span>
        {
            new(new string(' ', indent), _palette.Default),
            new(glyph, glyphStyle),
        };
        spans.AddRange(ParseInline(content));
        return spans;
    }

    private List<Span> ParseInline(string s)
    {
        var spans = new List<Span>();
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var consumed = TryConsumeInline(s, i, sb, spans);
            if (consumed > 0)
            {
                i += consumed;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        FlushPlain(sb, spans);
        return spans;
    }

    /// <summary>Try to match one of the inline patterns at <paramref name="i"/>.
    /// Returns how many source chars were consumed (0 = no match, fall through to literal).</summary>
    private int TryConsumeInline(string s, int i, System.Text.StringBuilder sb, List<Span> spans)
    {
        if (TryConsumeCode(s, i) is { } code)
        {
            FlushPlain(sb, spans);
            spans.Add(new Span(s[(i + 1)..code], _palette.Code));
            return code - i + 1;
        }
        if (TryConsumeBold(s, i) is { } bold)
        {
            FlushPlain(sb, spans);
            spans.Add(new Span(s[(i + 2)..bold], _palette.Bold));
            return bold - i + 2;
        }
        if (TryConsumeItalic(s, i) is { } italic)
        {
            FlushPlain(sb, spans);
            spans.Add(new Span(s[(i + 1)..italic], _palette.Italic));
            return italic - i + 1;
        }
        return 0;
    }

    private static int? TryConsumeCode(string s, int i)
    {
        if (s[i] != '`') return null;
        var end = s.IndexOf('`', i + 1);
        return end > i ? end : null;
    }

    private static int? TryConsumeBold(string s, int i)
    {
        if (i + 1 >= s.Length || s[i] != '*' || s[i + 1] != '*') return null;
        var end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
        return end > i ? end : null;
    }

    private static int? TryConsumeItalic(string s, int i)
    {
        // Single asterisk that's not part of a `**` pair on either side.
        if (s[i] != '*' || (i > 0 && s[i - 1] == '*') || i + 1 >= s.Length || s[i + 1] == '*') return null;
        var end = s.IndexOf('*', i + 1);
        if (end <= i) return null;
        if (end + 1 < s.Length && s[end + 1] == '*') return null;
        return end;
    }

    private void FlushPlain(System.Text.StringBuilder sb, List<Span> spans)
    {
        if (sb.Length == 0) return;
        spans.Add(new Span(sb.ToString(), _palette.Default));
        sb.Clear();
    }
}
