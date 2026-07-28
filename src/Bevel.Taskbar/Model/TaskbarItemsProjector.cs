using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Bevel.Core;

namespace Bevel.Taskbar;

/// <summary>
/// Projects the flat, per-window <see cref="TaskItemViewModel"/> collection into the taskbar strip's
/// display items (bevel-m2.10.3), collapsing multi-window apps into <see cref="TaskGroupViewModel"/>s
/// when grouping is enabled. When grouping is off, <see cref="Items"/> mirrors the source 1:1 (the
/// exact same instances, in order), so the ungrouped strip is byte-for-byte the pre-grouping
/// behaviour. Reconciles in place on every source membership change — reusing group view-models by
/// app id so their width/animation state and child subscriptions persist — and never rebuilds on mere
/// focus/title/icon changes (those flow through the shared window instances).
/// </summary>
public sealed class TaskbarItemsProjector : IDisposable
{
    /// <summary>WhenFull groups only once the strip carries more than this many windows. Keyed off the
    /// RAW window count (not the projected item count), so grouping — which shrinks the item count —
    /// can't feed back and flip the decision (the same class of feedback loop as bevel-m2.10.2).</summary>
    private const int WhenFullThreshold = 8;

    private readonly ObservableCollection<TaskItemViewModel> _source;
    private TaskbarGroupingMode _mode;
    private TaskbarWindowSort _sort = TaskbarWindowSort.OpenOrder;
    private bool _windowlessLast;

    public TaskbarItemsProjector(ObservableCollection<TaskItemViewModel> source, TaskbarGroupingMode mode = TaskbarGroupingMode.Never)
    {
        _source = source;
        _mode = mode;
        _source.CollectionChanged += OnSourceChanged;
        RePlan();
    }

    /// <summary>The display items — a mix of single-window buttons and app groups. Bound by the view.</summary>
    public ObservableCollection<ITaskbarItem> Items { get; } = new();

    /// <summary>Sets the grouping mode and re-plans immediately.</summary>
    public void SetGrouping(TaskbarGroupingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        RePlan();
    }

    /// <summary>Sets the button sort mode + the windowless-apps-last toggle and re-plans immediately.</summary>
    public void SetSort(TaskbarWindowSort sort, bool windowlessLast)
    {
        if (_sort == sort && _windowlessLast == windowlessLast) return;
        _sort = sort;
        _windowlessLast = windowlessLast;
        RePlan();
    }

    /// <summary>Resolves the mode to a concrete group/don't-group decision for the current window set.</summary>
    private bool ShouldGroup() => _mode switch
    {
        TaskbarGroupingMode.Always => true,
        TaskbarGroupingMode.WhenFull => _source.Count > WhenFullThreshold,
        _ => false,
    };

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RePlan();

    private void RePlan()
    {
        var plan = TaskbarGrouping.Plan(_source, ShouldGroup());

        // Reuse existing group view-models by key so width/animation + child subscriptions survive.
        var existingGroups = Items.OfType<TaskGroupViewModel>().ToDictionary(g => "g:" + g.AppId);

        var desired = new List<ITaskbarItem>(plan.Count);
        foreach (var entry in plan)
        {
            if (entry.IsGroup)
            {
                if (!existingGroups.TryGetValue(entry.Key, out var group))
                    group = new TaskGroupViewModel(entry.AppId!);
                group.SyncChildren(entry.Windows);
                desired.Add(group);
            }
            else
            {
                desired.Add(entry.Windows[0]);   // the window's own VM — identity preserved
            }
        }

        // Order the strip per the user's sort + windowless-last preference (bevel-ww71 follow-up). Stable:
        // within an equal key items keep their open order, so the classic positional feel survives when
        // sort is OpenOrder, and Name/windowless-last only reorder what they must.
        if (_windowlessLast || _sort != TaskbarWindowSort.OpenOrder)
            desired = SortItems(desired);

        // Apply in place: remove vanished items (detaching group child subscriptions), then move/insert
        // to match desired order. A full clear would drop bindings and restart the width animations.
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Items[i]))
            {
                if (Items[i] is TaskGroupViewModel g)
                    g.SyncChildren(Array.Empty<TaskItemViewModel>());   // unsubscribe its children
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var cur = Items.IndexOf(desired[i]);
            if (cur == i) continue;
            if (cur >= 0) Items.Move(cur, i);
            else Items.Insert(i, desired[i]);
        }
    }

    /// <summary>Stable sort: primary = windowless (app-presence) buttons last when enabled; secondary =
    /// the sort mode; tertiary = original (open) order, so equal-key items don't shuffle.</summary>
    private List<ITaskbarItem> SortItems(List<ITaskbarItem> items)
        => items
            .Select((it, i) => (it, i))
            .OrderBy(x => _windowlessLast && IsWindowless(x.it) ? 1 : 0)
            .ThenBy(x => _sort == TaskbarWindowSort.Name ? SortName(x.it) : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.i)
            .Select(x => x.it)
            .ToList();

    /// <summary>A group always has windows, so only a single app-presence button counts as windowless.</summary>
    private static bool IsWindowless(ITaskbarItem it) => it is TaskItemViewModel { IsAppPresence: true };

    /// <summary>App name for the Name sort — the friendly AppId, falling back to a window's title.</summary>
    private static string SortName(ITaskbarItem it) => it switch
    {
        TaskGroupViewModel g => g.AppId ?? string.Empty,
        TaskItemViewModel t => (string.IsNullOrEmpty(t.AppId) ? t.Title : t.AppId) ?? string.Empty,
        _ => string.Empty,
    };

    public void Dispose()
    {
        _source.CollectionChanged -= OnSourceChanged;
        foreach (var g in Items.OfType<TaskGroupViewModel>())
            g.SyncChildren(Array.Empty<TaskItemViewModel>());
        Items.Clear();
    }
}
