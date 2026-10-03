using Bevel.Core;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One bar's composed geometry. Replaces <c>TaskbarTheme</c>'s mutable statics: height is MEASURED
/// from the components actually on this bar, and the same composed value drives both the window
/// height and the OS work-area claim — one derivation, not two (spec §4.4). Per-bar rather than
/// static because two displays may carry different components (bevel-tjr2). <see cref="Contribute"/>
/// and the keyed <see cref="SetContribution"/>/<see cref="RemoveContribution"/> pair share ONE
/// backing store, so mixing them on one instance is safe — see <see cref="Contribute"/>'s doc.
/// </summary>
public sealed class BarGeometry
{
    /// <summary>Win2000 classic default, used when no component has contributed.</summary>
    private const int DefaultButtonHeight = 24;

    /// <summary>
    /// Reserved key under which <see cref="Contribute"/> stores its value in the SAME backing
    /// dictionary <see cref="SetContribution"/>/<see cref="RemoveContribution"/> use. One backing
    /// store, not two: an earlier version wrote <c>_buttonHeight</c> directly from <c>Contribute</c>
    /// and rebuilt it from <c>_byInstance</c> alone inside <c>Rederive</c>, so any bare
    /// <c>Contribute</c> call was silently discarded by a later keyed call on the same instance.
    /// </summary>
    private const string AnonymousKey = "__anonymous";

    private readonly int _rows;
    private readonly Dictionary<string, int> _byInstance = new(StringComparer.Ordinal);
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
    /// Records an anonymous contribution under the reserved <see cref="AnonymousKey"/>, in the same
    /// backing store <see cref="SetContribution"/>/<see cref="RemoveContribution"/> use — so mixing
    /// this with the keyed API on one instance is safe: neither can silently discard the other's
    /// value. Monotonic within its own key: the tallest bare <c>Contribute</c> call wins, so a
    /// single Big-tier strip grows the bar while a later clock asking for 16 does not shrink it. To
    /// let a contribution SHRINK, give it its own instance id and use <see cref="SetContribution"/>
    /// instead.
    /// </summary>
    public void Contribute(int heightDip)
    {
        if (heightDip <= 0) return;
        if (_byInstance.TryGetValue(AnonymousKey, out var current) && heightDip <= current) return;
        _byInstance[AnonymousKey] = heightDip;
        Recompute();
    }

    /// <summary>
    /// Replaces one instance's contribution in the same backing store <see cref="Contribute"/>
    /// writes to, and re-derives <see cref="ButtonHeight"/> from ALL contributions (keyed and
    /// anonymous alike). Needed because a single contribution is otherwise a monotonic max: without
    /// a way to replace or drop a specific instance's value, a Big→Normal tier change could never
    /// shrink the bar — exactly the stale-value failure class bevel-kclq records.
    /// </summary>
    public void SetContribution(string instanceId, int heightDip)
    {
        if (heightDip > 0) _byInstance[instanceId] = heightDip;
        else _byInstance.Remove(instanceId);
        Recompute();
    }

    /// <summary>Drops an instance's contribution (component removed or quarantined) and re-derives.</summary>
    public void RemoveContribution(string instanceId)
    {
        if (_byInstance.Remove(instanceId)) Recompute();
    }

    /// <summary>
    /// Live contributions, keyed and anonymous alike (the latter under the reserved
    /// <see cref="AnonymousKey"/>), so a row-count change can rebuild without losing any of them.
    /// </summary>
    public IReadOnlyDictionary<string, int> Contributions => _byInstance;

    /// <summary>Re-derives <see cref="ButtonHeight"/>/<see cref="TaskIconSize"/> as the max over ALL
    /// current contributions — the one place both <see cref="Contribute"/> and the keyed API read
    /// from, so the two can never disagree about what has been recorded.</summary>
    private void Recompute()
    {
        _buttonHeight = null;
        _iconSize = 16;
        foreach (var h in _byInstance.Values)
        {
            if (h <= 0) continue;
            if (_buttonHeight is { } current && h <= current) continue;
            _buttonHeight = h;
            _iconSize = IconSizeForButtonHeight(h);
        }
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
