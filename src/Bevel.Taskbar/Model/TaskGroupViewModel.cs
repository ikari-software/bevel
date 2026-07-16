using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace Bevel.Taskbar;

/// <summary>
/// A collapsed group of one app's windows, shown as a single taskbar button (bevel-m2.10.3): the app
/// icon + name + live window count, opening a flyout that lists the windows. Holds live references to
/// the same <see cref="TaskItemViewModel"/> instances the flat model owns and subscribes to their
/// focus/icon changes, so the group button's pressed state and icon track the windows without a
/// rebuild. Membership changes (a window of the app opens/closes) are applied by the projector via
/// <see cref="SyncChildren"/>.
/// </summary>
public sealed class TaskGroupViewModel : ObservableObject, ITaskbarItem
{
    private double _width;
    private double _opacity = 1;
    private bool _showLabel = true;

    public TaskGroupViewModel(string appId)
    {
        AppId = appId;
        DisplayName = TaskbarGrouping.DisplayName(appId);
    }

    public string AppId { get; }
    public string DisplayName { get; }

    public ObservableCollection<TaskItemViewModel> Windows { get; } = new();

    public int Count => Windows.Count;

    /// <summary>The group button's caption: "App (N)".</summary>
    public string Label => $"{DisplayName} ({Count})";

    /// <summary>Representative icon — the windows share an app, so the first window's icon stands in.</summary>
    public Bitmap? IconSource => Windows.FirstOrDefault()?.IconSource;

    /// <summary>Any child window focused → the group button shows the sunken/active state.</summary>
    public bool IsFocused => Windows.Any(w => w.IsFocused);

    /// <summary>Accessible summary for the group button.</summary>
    public string StatusText => $"{DisplayName} — {Count} windows (click to choose)";

    // ── ITaskbarItem (layout) — the same knobs a single button exposes ──
    public double Width { get => _width; set => SetProperty(ref _width, value); }
    public double Opacity { get => _opacity; set => SetProperty(ref _opacity, value); }

    public bool ShowLabel
    {
        get => _showLabel;
        set
        {
            if (!SetProperty(ref _showLabel, value)) return;
            OnPropertyChanged(nameof(IconMargin));
            OnPropertyChanged(nameof(ContentAlignment));
        }
    }

    // Groups don't animate out; a membership change rebuilds/updates them in place.
    public bool IsClosing => false;

    public Thickness IconMargin => ShowLabel ? new Thickness(0, 0, 4, 0) : default;
    public HorizontalAlignment ContentAlignment => ShowLabel ? HorizontalAlignment.Left : HorizontalAlignment.Center;

    /// <summary>
    /// Reconciles the group's child windows to <paramref name="desired"/> in place (preserving order),
    /// (un)subscribing to each child's focus/icon changes so the aggregates stay live. Returns true if
    /// membership actually changed.
    /// </summary>
    public bool SyncChildren(IReadOnlyList<TaskItemViewModel> desired)
    {
        var changed = false;

        // Remove windows no longer in the group.
        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Windows[i]))
            {
                Windows[i].PropertyChanged -= OnChildChanged;
                Windows.RemoveAt(i);
                changed = true;
            }
        }

        // Insert new windows in desired order.
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Windows.Count && ReferenceEquals(Windows[i], desired[i]))
                continue;
            if (Windows.Contains(desired[i]))
            {
                Windows.Move(Windows.IndexOf(desired[i]), i);
            }
            else
            {
                desired[i].PropertyChanged += OnChildChanged;
                Windows.Insert(i, desired[i]);
            }
            changed = true;
        }

        if (changed)
            RaiseAggregates();
        return changed;
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskItemViewModel.IsFocused))
            OnPropertyChanged(nameof(IsFocused));
        else if (e.PropertyName is nameof(TaskItemViewModel.IconSource))
            OnPropertyChanged(nameof(IconSource));
    }

    private void RaiseAggregates()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(StatusText));
    }
}
