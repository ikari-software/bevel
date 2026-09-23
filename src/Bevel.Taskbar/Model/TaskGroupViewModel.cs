using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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

        // App-scoped right-click verbs — they act on every window of this one app (the group is keyed
        // by AppId), which is what makes the grouped button's context menu app-specific. Each delegates
        // to the child window's own command so the IWindowManager wiring lives in one place.
        MinimizeAllCommand = new AsyncRelayCommand(() => { RunOnAll(w => w.MinimizeCommand); return Task.CompletedTask; });
        RestoreAllCommand = new AsyncRelayCommand(() => { RunOnAll(w => w.RestoreCommand); return Task.CompletedTask; });
        CloseAllCommand = new AsyncRelayCommand(() => { RunOnAll(w => w.CloseCommand); return Task.CompletedTask; });
        // Quit acts on the whole app (all members share one bundle) — delegate to any member's command.
        QuitCommand = new AsyncRelayCommand(() => { Windows.FirstOrDefault()?.QuitCommand.Execute(null); return Task.CompletedTask; });
        ForceQuitCommand = new AsyncRelayCommand(() => { Windows.FirstOrDefault()?.ForceQuitCommand.Execute(null); return Task.CompletedTask; });
    }

    /// <summary>The app's bundle id (all members share it) for app-level actions like Quit.</summary>
    public string? BundleId => Windows.FirstOrDefault()?.BundleId;

    public string AppId { get; }
    public string DisplayName { get; }

    /// <summary>Minimize / restore / close every window of this app at once (grouped-button context menu).</summary>
    public ICommand MinimizeAllCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand CloseAllCommand { get; }

    /// <summary>Quit / force-quit the whole app (bevel-ww71).</summary>
    public ICommand QuitCommand { get; }
    public ICommand ForceQuitCommand { get; }

    /// <summary>Runs one per-window command across the whole group. Snapshots the collection first —
    /// closing/minimizing mutates <see cref="Windows"/> as the shell reacts to each window op.</summary>
    private void RunOnAll(Func<TaskItemViewModel, ICommand> pick)
    {
        foreach (var w in Windows.ToArray())
        {
            var cmd = pick(w);
            if (cmd.CanExecute(null)) cmd.Execute(null);
        }
    }

    public ObservableCollection<TaskItemViewModel> Windows { get; } = new();

    public int Count => Windows.Count;

    /// <summary>The group button's caption: "App (N)".</summary>
    public string Label => $"{DisplayName} ({Count})";

    /// <summary>Representative icon — the windows share an app, so the first window's icon stands in.</summary>
    public Bitmap? IconSource => Windows.FirstOrDefault()?.IconSource;

    /// <summary>Any child window focused → the group button shows the sunken/active state.</summary>
    public bool IsFocused => Windows.Any(w => w.IsFocused);

    /// <summary>
    /// The app's unread badge (bevel-ijln). Every member window belongs to ONE app, so they all carry
    /// the same platform label — take the first non-empty rather than summing. Deliberately NOT the
    /// window count: <see cref="Count"/> is the glomming count (R-TB-6) and lives in <see cref="Label"/>.
    /// </summary>
    public string? BadgeText => Windows.FirstOrDefault(w => w.HasBadge)?.BadgeText;

    /// <summary>The group badge as a number when it parses as one, else null.</summary>
    public int? BadgeCount => int.TryParse(BadgeText, out var n) && n > 0 ? n : null;

    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

    /// <summary>Accessible summary for the group button.</summary>
    public string StatusText => HasBadge
        ? $"{DisplayName} — {Count} windows, {BadgeText} unread (click to choose)"
        : $"{DisplayName} — {Count} windows (click to choose)";

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
        else if (e.PropertyName is nameof(TaskItemViewModel.BadgeText))
            RaiseBadge();
    }

    private void RaiseBadge()
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(HasBadge));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseAggregates()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(StatusText));
        RaiseBadge();
    }
}
