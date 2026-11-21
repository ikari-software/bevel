using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Bevel.FileManager.Components;

/// <summary>
/// The one filename label every item view uses (bevel-3fgd). A <see cref="TextBlock"/> that records
/// whether its last layout cut the name off (<see cref="IsTrimmed"/>) and, only then, tooltips the full
/// name, so a cut-off name is always readable on hover and a name that fits has no tooltip. Give it a
/// width to trim to (a DockPanel/Grid slot, never a horizontal StackPanel, which measures at infinity)
/// and set <see cref="TextBlock.TextTrimming"/> to an ellipsis mode.
/// </summary>
internal sealed class NameLabel : TextBlock
{
    // Keep TextBlock's theme: without this the subclass finds no ControlTheme of its own.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>True when the current layout does not show the whole name.</summary>
    public bool IsTrimmed { get; private set; }

    bool _tipShown;

    protected override TextLayout CreateTextLayout(string? text)
    {
        var layout = base.CreateTextLayout(text);
        bool collapsed = false;
        foreach (var line in layout.TextLines)
            if (line.HasCollapsed) { collapsed = true; break; }
        IsTrimmed = collapsed;

        // Wrapped and line-limited (icon views, bevel-m9jy): Avalonia drops the lines past MaxLines but
        // adds the ellipsis only when the last kept line is cut INSIDE a word; a cut between words shows
        // two clean lines and silently loses the rest of the name. Detect the lost text and lay it out
        // again with the line break made explicit and wrapping off, so the trimmer collapses line 2.
        if (collapsed || string.IsNullOrEmpty(text) || TextWrapping == TextWrapping.NoWrap || MaxLines <= 0)
            return layout;
        int shown = 0;
        foreach (var line in layout.TextLines) shown += line.Length;
        if (shown >= text.Length || layout.TextLines.Count < 2) return layout;

        IsTrimmed = true;
        int head = 0;
        for (int i = 0; i < layout.TextLines.Count - 1; i++) head += layout.TextLines[i].Length;
        var forced = text[..head].TrimEnd() + "\n" + text[head..].TrimStart();
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        double maxW = layout.MaxWidth, maxH = layout.MaxHeight;
        layout.Dispose();
        return new TextLayout(forced, typeface, FontFeatures, FontSize, Foreground, TextAlignment,
            TextWrapping.NoWrap, TextTrimming, TextDecorations, FlowDirection,
            maxW, maxH, LineHeight, LetterSpacing, MaxLines, null);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        // Arrange runs after the final-width layout, so IsTrimmed reflects what is drawn.
        if (IsTrimmed != _tipShown)
        {
            _tipShown = IsTrimmed;
            ToolTip.SetTip(this, IsTrimmed ? Text : null);
        }
        else if (_tipShown && !Equals(ToolTip.GetTip(this), Text))
        {
            ToolTip.SetTip(this, Text);   // still trimmed, but the name changed
        }
        return size;
    }
}
