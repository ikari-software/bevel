using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
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
        UpdateHueSubscriptions();   // keep colour-sort re-ordering fresh as icons load
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
            .ThenBy(x => _sort == TaskbarWindowSort.Colour ? IconHue(x.it) : 0d)
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

    // ── Colour sort (a rainbow taskbar) ─────────────────────────────────────

    /// <summary>Icon hue (0–360, red→violet) for the Colour sort; items with no colourful icon (still
    /// loading, greyscale, transparent) sort to the end.</summary>
    private static double IconHue(ITaskbarItem it) => it switch
    {
        TaskItemViewModel t => HueOf(t.IconSource),
        TaskGroupViewModel g => HueOf(g.IconSource),
        _ => double.MaxValue,
    };

    // Icons are cached per bundle id and reused, so cache the hue per Bitmap and only pixel-scan once.
    private static readonly ConditionalWeakTable<Bitmap, object> _hueCache = new();

    private static double HueOf(Bitmap? bmp)
    {
        if (bmp is null) return double.MaxValue;
        if (_hueCache.TryGetValue(bmp, out var cached)) return (double)cached;
        var hue = ComputeHue(bmp);
        _hueCache.AddOrUpdate(bmp, hue);
        return hue;
    }

    /// <summary>Dominant hue of an icon: the saturation-and-alpha-weighted average of its opaque pixels,
    /// so a mostly-grey icon with one coloured accent sorts by the accent, not muddy grey.</summary>
    internal static double ComputeHue(Bitmap bmp)
    {
        try
        {
            var size = bmp.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return double.MaxValue;
            int stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, stride); }
            finally { handle.Free(); }

            double r = 0, g = 0, b = 0, wsum = 0;
            for (int i = 0; i + 3 < buffer.Length; i += 4)   // BGRA
            {
                byte a = buffer[i + 3];
                if (a < 32) continue;
                double bb = buffer[i] / 255.0, gg = buffer[i + 1] / 255.0, rr = buffer[i + 2] / 255.0;
                double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double w = sat * (a / 255.0);
                r += rr * w; g += gg * w; b += bb * w; wsum += w;
            }
            if (wsum < 1e-6) return double.MaxValue;   // greyscale / transparent → sort last
            return RgbToHue(r / wsum, g / wsum, b / wsum);
        }
        catch { return double.MaxValue; }
    }

    /// <summary>RGB (0–1) → hue in degrees (0–360). Grey returns 0.</summary>
    internal static double RgbToHue(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 1e-6) return 0;
        double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    // When ordering by colour, an icon that loads AFTER the plan must re-order the strip — subscribe to
    // each source item's IconSource change while (and only while) colour sort is active.
    private readonly HashSet<TaskItemViewModel> _hueSubs = new();

    private void UpdateHueSubscriptions()
    {
        if (_sort != TaskbarWindowSort.Colour)
        {
            foreach (var t in _hueSubs) t.PropertyChanged -= OnItemPropertyChanged;
            _hueSubs.Clear();
            return;
        }
        var current = new HashSet<TaskItemViewModel>(_source);
        foreach (var gone in _hueSubs.Where(t => !current.Contains(t)).ToList())
        {
            gone.PropertyChanged -= OnItemPropertyChanged;
            _hueSubs.Remove(gone);
        }
        foreach (var t in _source)
            if (_hueSubs.Add(t)) t.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_sort == TaskbarWindowSort.Colour && e.PropertyName == nameof(TaskItemViewModel.IconSource))
            RePlan();
    }

    public void Dispose()
    {
        _source.CollectionChanged -= OnSourceChanged;
        foreach (var t in _hueSubs) t.PropertyChanged -= OnItemPropertyChanged;
        _hueSubs.Clear();
        foreach (var g in Items.OfType<TaskGroupViewModel>())
            g.SyncChildren(Array.Empty<TaskItemViewModel>());
        Items.Clear();
    }
}
