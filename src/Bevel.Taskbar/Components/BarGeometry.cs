using Bevel.Core;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One bar's composed geometry. Replaces <c>TaskbarTheme</c>'s mutable statics: height is MEASURED
/// from the components actually on this bar, and the same composed value drives both the window
/// height and the OS work-area claim — one derivation, not two (spec §4.4). Per-bar rather than
/// static because two displays may carry different components (bevel-tjr2).
/// </summary>
public sealed class BarGeometry
{
    /// <summary>Win2000 classic default, used when no component has contributed.</summary>
    private const int DefaultButtonHeight = 24;

    private readonly int _rows;
    // Null means "nothing has contributed yet" — kept DISTINCT from the default value, or a
    // contribution smaller than the default (Small tier = 18) would be silently ignored.
    private int? _buttonHeight;
    private int _iconSize = 16;

    public BarGeometry(int rows) => _rows = Math.Max(1, rows);

    /// <summary>Window-button height in logical px: the tallest contribution on this bar.</summary>
    public int ButtonHeight => _buttonHeight ?? DefaultButtonHeight;

    /// <summary>Task-button glyph edge in logical px, derived from the winning button height.</summary>
    public int TaskIconSize => _iconSize;

    /// <summary>Height per button row: the button plus its 4px (2+2) vertical margin.</summary>
    public int RowHeight => ButtonHeight + 4;

    /// <summary>Single-row bar height in logical px: one row plus the 2px chrome inset.</summary>
    public int TaskbarHeight => RowHeight + 2;

    /// <summary>Total bar height for this bar's row count.</summary>
    public int Height => TaskbarHeight + (_rows - 1) * RowHeight;

    /// <summary>
    /// Records one component's desired button height. The tallest wins, so a single Big-tier strip
    /// grows the bar while a clock asking for 16 does not shrink it.
    /// </summary>
    public void Contribute(int heightDip)
    {
        if (heightDip <= 0) return;
        if (_buttonHeight is { } current && heightDip <= current) return;
        _buttonHeight = heightDip;
        _iconSize = IconSizeForButtonHeight(heightDip);
    }

    /// <summary>
    /// Replaces one instance's contribution and re-derives from ALL of them. Needed because
    /// <see cref="Contribute"/> is a monotonic max: without this, a Big→Normal tier change could
    /// never shrink the bar — exactly the stale-value failure class bevel-kclq records.
    /// </summary>
    public void SetContribution(string instanceId, int heightDip)
    {
        if (heightDip > 0) _byInstance[instanceId] = heightDip;
        else _byInstance.Remove(instanceId);
        Rederive();
    }

    /// <summary>Drops an instance's contribution (component removed or quarantined) and re-derives.</summary>
    public void RemoveContribution(string instanceId)
    {
        if (_byInstance.Remove(instanceId)) Rederive();
    }

    private readonly Dictionary<string, int> _byInstance = new(StringComparer.Ordinal);

    /// <summary>Live per-instance contributions, so a row-count change can rebuild without losing them.</summary>
    public IReadOnlyDictionary<string, int> Contributions => _byInstance;

    private void Rederive()
    {
        _buttonHeight = null;
        _iconSize = 16;
        foreach (var h in _byInstance.Values) Contribute(h);
    }

    /// <summary>Button height for a user-facing size tier. Normal reproduces Win2000's 24/28/30.</summary>
    public static int ButtonHeightFor(TaskbarButtonSize size) => size switch
    {
        TaskbarButtonSize.Small => 18,
        TaskbarButtonSize.Large => 30,
        TaskbarButtonSize.Big => 40,
        _ => DefaultButtonHeight,
    };

    /// <summary>Glyph edge for a size tier. Icons arrive at 64px, so every value is a downscale.</summary>
    public static int TaskIconSizeFor(TaskbarButtonSize size) => size switch
    {
        TaskbarButtonSize.Large => 24,
        TaskbarButtonSize.Big => 32,
        _ => 16,
    };

    private static int IconSizeForButtonHeight(int h) => h switch
    {
        >= 40 => 32,
        >= 30 => 24,
        _ => 16,
    };
}
