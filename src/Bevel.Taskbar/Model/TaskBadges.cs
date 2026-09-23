using System.Collections.Generic;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Matches the platform's app-badge snapshot (<see cref="IAppBadgeSource"/>, bevel-ijln) onto taskbar
/// buttons. Pure and UI-free so the matching rules are unit-testable without a window: the poll loop
/// builds the index off-thread and only <see cref="Apply"/> touches view-models on the UI thread.
///
/// <para>Keying: a badge is matched on the bundle id first (exact, machine-stable) and on the app's
/// display name second — the window enumeration's <c>AppId</c> is the friendly name on macOS, and the
/// Dock reports the same localized string, so the fallback closes the gap when a bundle id is absent.
/// Names match case-insensitively; bundle ids do not (they are case-sensitive identifiers).</para>
/// </summary>
public static class TaskBadges
{
    /// <summary>
    /// Folds a badge snapshot into a key → label lookup. Both the bundle id and the display name of
    /// each badge become keys, so a button matches on whichever it carries. Empty labels are dropped —
    /// there is no such thing as a blank badge.
    /// </summary>
    public static Dictionary<string, string> Index(IReadOnlyList<AppBadge>? badges)
    {
        // OrdinalIgnoreCase covers the display-name keys; a bundle id collides with nothing in practice.
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (badges is null) return index;

        foreach (var b in badges)
        {
            if (string.IsNullOrWhiteSpace(b.Label)) continue;
            var label = b.Label.Trim();
            if (!string.IsNullOrEmpty(b.BundleId)) index[b.BundleId] = label;
            if (!string.IsNullOrEmpty(b.AppName)) index.TryAdd(b.AppName, label);
        }
        return index;
    }

    /// <summary>The label for one button, or null when the platform publishes no badge for its app.</summary>
    public static string? Lookup(TaskItemViewModel item, IReadOnlyDictionary<string, string> index)
    {
        foreach (var key in item.BadgeKeys())
            if (index.TryGetValue(key, out var label))
                return label;
        return null;
    }

    /// <summary>
    /// UI thread. Pushes the snapshot onto every button, CLEARING buttons whose app no longer badges —
    /// a stale count is worse than none. Each push is idempotent, so an unchanged snapshot is free.
    /// </summary>
    public static void Apply(IEnumerable<TaskItemViewModel> items, IReadOnlyDictionary<string, string> index)
    {
        foreach (var item in items)
            item.ApplyBadge(Lookup(item, index));
    }
}
