using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

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
    private readonly ObservableCollection<TaskItemViewModel> _source;
    private bool _grouping;

    public TaskbarItemsProjector(ObservableCollection<TaskItemViewModel> source, bool grouping = false)
    {
        _source = source;
        _grouping = grouping;
        _source.CollectionChanged += OnSourceChanged;
        RePlan();
    }

    /// <summary>The display items — a mix of single-window buttons and app groups. Bound by the view.</summary>
    public ObservableCollection<ITaskbarItem> Items { get; } = new();

    /// <summary>Turns grouping on/off and re-plans immediately.</summary>
    public void SetGrouping(bool grouping)
    {
        if (_grouping == grouping) return;
        _grouping = grouping;
        RePlan();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RePlan();

    private void RePlan()
    {
        var plan = TaskbarGrouping.Plan(_source, _grouping);

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

    public void Dispose()
    {
        _source.CollectionChanged -= OnSourceChanged;
        foreach (var g in Items.OfType<TaskGroupViewModel>())
            g.SyncChildren(Array.Empty<TaskItemViewModel>());
        Items.Clear();
    }
}
