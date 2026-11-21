using Avalonia;
using Avalonia.Controls;
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
        bool trimmed = false;
        foreach (var line in layout.TextLines)
            if (line.HasCollapsed) { trimmed = true; break; }
        IsTrimmed = trimmed;
        return layout;
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
