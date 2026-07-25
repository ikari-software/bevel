using System.Collections.Generic;
using System.Linq;

namespace Bevel.Taskbar;

/// <summary>
/// One planned entry in the grouped taskbar strip (bevel-m2.10.3): either a single window (Key
/// <c>w:&lt;windowId&gt;</c>, one window) or a collapsed app group (Key <c>g:&lt;appId&gt;</c>, ≥2
/// windows). The plan is a pure function of the flat window list + the grouping flag, so it is unit
/// testable without a view; the projector turns it into live view-models.
/// </summary>
public readonly record struct TaskbarPlanEntry(string Key, string? AppId, IReadOnlyList<TaskItemViewModel> Windows)
{
    public bool IsGroup => Key.Length > 0 && Key[0] == 'g';
}

/// <summary>Pure grouping policy for the taskbar strip (bevel-m2.10.3).</summary>
public static class TaskbarGrouping
{
    /// <summary>A human-friendly app name from a bundle id: the last non-empty dot-segment, title-cased
    /// (<c>com.google.Chrome → Chrome</c>). Falls back to the id, then to "App".</summary>
    public static string DisplayName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "App";
        var seg = appId.Split('.').LastOrDefault(s => s.Length > 0) ?? appId;
        return seg.Length == 0 ? "App" : char.ToUpperInvariant(seg[0]) + seg[1..];
    }

    /// <summary>
    /// Plans the grouped strip from the flat window list, preserving order. When
    /// <paramref name="grouping"/> is off, every window passes through as its own entry (identical to
    /// the ungrouped strip). When on, an app with ≥2 live windows collapses into one group placed at
    /// its FIRST window's position; the app's other windows are folded into that group. Windows with
    /// no app id, and windows currently animating out (<see cref="TaskItemViewModel.IsClosing"/>),
    /// never form or join a group — closing windows still pass through individually so they finish
    /// their exit animation.
    /// </summary>
    public static IReadOnlyList<TaskbarPlanEntry> Plan(IReadOnlyList<TaskItemViewModel> windows, bool grouping)
    {
        var result = new List<TaskbarPlanEntry>(windows.Count);

        // App-presence dedup (bevel-ww71): a windowless-app button is suppressed whenever a REAL window
        // for the same app is present — so the merge transition (a window opens while the presence entry
        // is still in the set for one frame) never shows a duplicate, and grouping-mode is irrelevant.
        var appsWithWindow = new HashSet<string>();
        foreach (var w in windows)
            if (!w.IsClosing && !w.IsAppPresence && w.AppId is { Length: > 0 } id)
                appsWithWindow.Add(id);

        bool Suppressed(TaskItemViewModel w) =>
            w.IsAppPresence && w.AppId is { Length: > 0 } a && appsWithWindow.Contains(a);

        // Which apps have ≥2 live (non-closing, real) windows — the group candidates. Presence entries
        // are synthetic singletons and never participate in grouping.
        var groups = grouping
            ? windows.Where(w => !w.IsClosing && !w.IsAppPresence && !string.IsNullOrEmpty(w.AppId))
                     .GroupBy(w => w.AppId!)
                     .Where(g => g.Count() >= 2)
                     .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskItemViewModel>)g.ToList())
            : new Dictionary<string, IReadOnlyList<TaskItemViewModel>>();

        var emitted = new HashSet<string>();
        foreach (var w in windows)
        {
            if (Suppressed(w))
                continue;
            if (!w.IsClosing && !w.IsAppPresence && w.AppId is { Length: > 0 } appId && groups.TryGetValue(appId, out var members))
            {
                if (emitted.Add(appId))
                    result.Add(new TaskbarPlanEntry("g:" + appId, appId, members));
                // subsequent windows of this app are already folded into the group
            }
            else
            {
                result.Add(new TaskbarPlanEntry("w:" + w.Id.Value, w.AppId, new[] { w }));
            }
        }

        return result;
    }
}
