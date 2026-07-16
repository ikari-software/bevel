namespace Bevel.Taskbar;

/// <summary>
/// A taskbar-strip entry the layout pass can size — either a single-window button
/// (<see cref="TaskItemViewModel"/>) or a collapsed app group (<see cref="TaskGroupViewModel"/>,
/// bevel-m2.10.3). Only the layout-facing surface is shared; the view selects a template per
/// concrete type. Keeping both behind one interface lets <c>LayoutButtons</c> size a mixed strip
/// (groups count as one button) without caring which kind each item is.
/// </summary>
public interface ITaskbarItem
{
    /// <summary>Animated button width (logical px) the layout pass pushes; bound through a transition.</summary>
    double Width { get; set; }

    /// <summary>Companion fade for the width slide.</summary>
    double Opacity { get; set; }

    /// <summary>Whether the entry shows its text label (cleared in the icon-only tier).</summary>
    bool ShowLabel { get; set; }

    /// <summary>True while animating out; the layout pass skips it so it isn't snapped back.</summary>
    bool IsClosing { get; }
}
