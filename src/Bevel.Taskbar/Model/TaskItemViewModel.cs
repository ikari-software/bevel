using System.Windows.Input;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// One taskbar window button, as data. No visual-tree work happens here — Title / focus /
/// icon update in place and the data-bound view reflects them. The icon (decoded off-thread
/// from the window's <see cref="ForeignWindow.IconPng"/>) is pushed to <see cref="IconSource"/>
/// on the UI thread by <see cref="ShellModel"/>.
/// </summary>
public sealed class TaskItemViewModel : ObservableObject, ITaskbarItem
{
    private readonly IWindowManager _windows;
    private string _title = "";
    private bool _isFocused;
    private bool _isMinimized;
    private double _width;
    private double _lastLiveWidth;
    private double _opacity;
    private bool _isClosing;
    private Bitmap? _iconSource;
    private bool _showLabel = true;

    public TaskItemViewModel(ForeignWindow w, IWindowManager windows)
    {
        _windows = windows;
        Id = w.Id;
        AppId = w.AppId;
        IsAppPresence = w.IsAppPresence;
        // Bundle id for app-level actions (Quit). Prefer the explicit field; for a windowless entry the
        // id is "app:<bundle>", so parse it as a fallback.
        BundleId = w.BundleId ?? (w.Id.Value.StartsWith("app:") ? w.Id.Value["app:".Length..] : null);
        Update(w);
        if (w.IsFocused)
            SetFocused(true);
        ActivateCommand = new AsyncRelayCommand(ToggleAsync);
        CloseCommand = new AsyncRelayCommand(() => _windows.CloseAsync(Id));
        MinimizeCommand = new AsyncRelayCommand(() => _windows.MinimizeAsync(Id));
        RestoreCommand = new AsyncRelayCommand(() => _windows.RestoreAsync(Id));
        QuitCommand = new AsyncRelayCommand(() => TerminateAsync(force: false));
        ForceQuitCommand = new AsyncRelayCommand(() => TerminateAsync(force: true));
    }

    private Task TerminateAsync(bool force) =>
        string.IsNullOrEmpty(BundleId) ? Task.CompletedTask : _windows.TerminateAppAsync(BundleId, force);

    public ForeignWindowId Id { get; }

    /// <summary>Owning app's identifier (bundle id), used to collapse multi-window apps into a group
    /// (bevel-m2.10.3). Null/empty for windows with no known app — those never group.</summary>
    public string? AppId { get; }

    /// <summary>True for a running-but-windowless app entry (bevel-ww71): rendered dim + icon-only, and a
    /// click just reopens the app (no minimize/toggle, since there's no window).</summary>
    public bool IsAppPresence { get; }

    /// <summary>Owning app's bundle id (stable key), used by <see cref="QuitCommand"/>. Null when unknown.</summary>
    public string? BundleId { get; }

    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsFocused
    {
        get => _isFocused;
        private set
        {
            if (!SetProperty(ref _isFocused, value)) return;
            OnPropertyChanged(nameof(TitleWeight));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsMinimized
    {
        get => _isMinimized;
        private set
        {
            if (!SetProperty(ref _isMinimized, value)) return;
            OnPropertyChanged(nameof(ContentOpacity));
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public Bitmap? IconSource { get => _iconSource; set => SetProperty(ref _iconSource, value); }

    /// <summary>
    /// Whether the button shows its text label. The layout pass clears this when buttons shrink past
    /// the icon-only threshold (bevel-m2.10), collapsing the button to a centred icon — the hover
    /// tooltip still carries the full title, so nothing is lost. Drives <see cref="IconMargin"/> and
    /// <see cref="ContentAlignment"/> so the icon recentres when the label goes.
    /// </summary>
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

    /// <summary>Icon gets right padding only when a label follows it; icon-only buttons centre bare.</summary>
    public Thickness IconMargin => ShowLabel ? new Thickness(0, 0, 4, 0) : default;

    /// <summary>Left-align icon+label (Win2000 convention); centre the lone icon when the label is gone.</summary>
    public HorizontalAlignment ContentAlignment => ShowLabel ? HorizontalAlignment.Left : HorizontalAlignment.Center;

    /// <summary>Bold label for the focused window (Win2000/XP taskbar convention).</summary>
    public FontWeight TitleWeight => IsFocused ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>Windowless "app-presence" entries (dock-dots) render their label in italic — a running
    /// app with no open window reads as "present but idle" (bevel-ww71). Immutable, set at construction.</summary>
    public FontStyle TitleStyle => IsAppPresence ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>Minimized windows remain actionable but their content is visibly recessed.</summary>
    public double ContentOpacity => IsAppPresence ? 0.6 : IsMinimized ? 0.55 : 1;

    /// <summary>Accessible state and the action clicking the button will perform. The unread badge
    /// (bevel-ijln) joins it so a screen reader announces the count the pill shows visually.</summary>
    public string StatusText
    {
        get
        {
            var state = IsMinimized
                ? $"{Title} — Minimized (click to restore)"
                : IsFocused
                    ? $"{Title} — Active (click to minimize)"
                    : $"{Title} — Open (click to activate)";
            return HasBadge ? $"{state} — {BadgeText} unread" : state;
        }
    }

    /// <summary>
    /// Animated button width (logical px). The template binds Width here through a transition, so
    /// the signature XP taskbar move — width grows from 0 when a window opens and shrinks to 0
    /// when it closes, sliding the neighbours — falls out of a plain property change. Starts at 0
    /// (grow-in); the layout pass pushes the shared shrink-to-fit target to every live button.
    /// </summary>
    public double Width
    {
        get => _width;
        set
        {
            if (!IsClosing && value > 0)
                _lastLiveWidth = value;
            SetProperty(ref _width, value);
        }
    }

    /// <summary>Companion fade for the width slide (0→1 in, 1→0 out); bound through a transition.</summary>
    public double Opacity { get => _opacity; set => SetProperty(ref _opacity, value); }

    /// <summary>
    /// True once the window has closed: the button is animating out (Width→0, Opacity→0) and the
    /// layout pass skips it so it isn't snapped back to the target width mid-exit. <see cref="ShellModel"/>
    /// removes it from the collection only after the animation completes.
    /// </summary>
    public bool IsClosing { get => _isClosing; set => SetProperty(ref _isClosing, value); }

    private int _closeGeneration;

    /// <summary>Identifies the current close episode. <see cref="ShellModel"/> captures this when it
    /// schedules the exit-animation removal and only completes the removal if it still matches — so a
    /// close -> revive -> close within the 160ms grace window can't let the first timer remove the
    /// button mid-second-animation. Bumped when a close starts (<see cref="BeginCloseEpoch"/>) and
    /// when a revive cancels one.</summary>
    internal int CloseGeneration => _closeGeneration;

    /// <summary>Opens a new close episode and returns its token for the removal timer to capture.</summary>
    internal int BeginCloseEpoch() => ++_closeGeneration;

    /// <summary>
    /// Cancels a pending exit when the same native window reappears before the grace period
    /// expires. This preserves the VM/container identity, so a transient enumeration gap or
    /// title-change filter does not turn an update into a replacement animation.
    /// </summary>
    public void Revive()
    {
        if (!IsClosing) return;
        _closeGeneration++; // invalidate any pending exit-animation removal timer
        IsClosing = false;
        Width = _lastLiveWidth;
        Opacity = 1;
    }

    /// <summary>Command bound to the button (Win2000 toggle — see <see cref="ToggleAsync"/>).</summary>
    public ICommand ActivateCommand { get; }

    /// <summary>Closes this window (middle-click on its taskbar button, bevel-cust.buttons).</summary>
    public ICommand CloseCommand { get; }

    /// <summary>Right-click context-menu verbs (bevel-cust.ctxmenu): the taskbar-button system menu.</summary>
    public ICommand MinimizeCommand { get; }
    public ICommand RestoreCommand { get; }

    /// <summary>Quit the owning app (graceful terminate) — the app-level action every button offers.</summary>
    public ICommand QuitCommand { get; }

    /// <summary>Force-quit the owning app (Alt/Option in the menu).</summary>
    public ICommand ForceQuitCommand { get; }

    /// <summary>Refreshes the label/focus/minimized state from a fresh window snapshot in place,
    /// keeping the same VM object so the list row and its icon survive the update.</summary>
    public void Update(ForeignWindow w)
    {
        Title = string.IsNullOrEmpty(w.Title) ? w.AppId ?? "" : w.Title;
        IsMinimized = w.IsMinimized;
        // IsFocused is owned by ShellModel.ApplyExclusiveFocus — per-event isFocused flags
        // go stale and were leaving multiple buttons in the pressed state.
    }

    /// <summary>Sets focus from the shell's exclusive foreground projection.</summary>
    public void SetFocused(bool focused) => IsFocused = focused;

    // ── Unread / attention badge (bevel-ijln) ───────────────────────────
    //
    // The count an app publishes for itself — Slack's unread, Mail's inbox — sourced from the
    // platform (on macOS, the Dock's accessibility tree; see IAppBadgeSource) and pushed here by
    // ShellModel off the poll loop. NEVER inferred from the title: no source, no badge.

    private string? _badgeText;

    /// <summary>
    /// The badge label exactly as the platform publishes it, or null when this app shows none.
    /// Usually a count ("3"), but a platform may hand over an ellipsized or non-numeric label
    /// (macOS reports "..82" for a four-digit badge), so this is a string and is shown verbatim.
    /// </summary>
    public string? BadgeText
    {
        get => _badgeText;
        private set
        {
            if (!SetProperty(ref _badgeText, value)) return;
            OnPropertyChanged(nameof(BadgeCount));
            OnPropertyChanged(nameof(HasBadge));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>The badge as a number when it parses as one, else null — callers that want an int
    /// must cope with null rather than invent a value.</summary>
    public int? BadgeCount => int.TryParse(BadgeText, out var n) && n > 0 ? n : null;

    /// <summary>Drives the pill's visibility; false whenever the app publishes no badge.</summary>
    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

    /// <summary>Pushes (or with null/empty clears) the platform badge label. Idempotent — an
    /// unchanged label raises nothing, so the 2s poll doesn't churn bindings.</summary>
    public void ApplyBadge(string? label) =>
        BadgeText = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    /// <summary>The keys this button can be matched against a platform badge by: the bundle id (exact,
    /// preferred) and the friendly app id the enumeration carries.</summary>
    internal IEnumerable<string> BadgeKeys()
    {
        if (!string.IsNullOrEmpty(BundleId)) yield return BundleId;
        if (!string.IsNullOrEmpty(AppId)) yield return AppId;
    }

    /// <summary>
    /// Classic Win2000 task-button toggle (minimizing never removes the button):
    ///   minimized → restore + raise (a raise alone won't de-miniaturize);
    ///   focused   → minimize;
    ///   otherwise → activate.
    /// </summary>
    private async Task ToggleAsync()
    {
        // App-presence button (bevel-ww71): no window to minimize/toggle — a click just reopens the app.
        // The helper's activateWindow resolves the "app:<bundle>" id to NSRunningApplication.activate().
        if (IsAppPresence)
        {
            TaskbarLog.Debug($"CLICK id={Id.Value} app-presence -> reopen");
            try { await _windows.ActivateAsync(Id); }
            catch (Exception ex) { TaskbarLog.Debug($"CLICK app-presence failed id={Id.Value}: {ex.Message}"); }
            return;
        }

        var action = IsMinimized ? "restore+activate" : IsFocused ? "minimize" : "activate";
        TaskbarLog.Debug($"CLICK id={Id.Value} title='{Title}' min={IsMinimized} focus={IsFocused} -> {action}");
        try
        {
            if (IsMinimized)
            {
                IsMinimized = false;   // optimistic: the button un-dims on the click, not a round-trip later
                // ONE atomic op (bevel-nxic): the helper de-miniaturizes then raises the window LAST, so a
                // second RPC no longer races the de-miniaturize animation and lands the window mid-stack.
                await _windows.RestoreAndActivateAsync(Id);
            }
            else if (IsFocused)
            {
                IsMinimized = true; IsFocused = false;   // optimistic: button un-presses + dims immediately
                await _windows.MinimizeAsync(Id);
            }
            else
            {
                await _windows.ActivateAsync(Id);
            }
            TaskbarLog.Debug($"CLICK done id={Id.Value} ({action})");
        }
        catch (Exception ex)
        {
            TaskbarLog.Debug($"CLICK FAILED id={Id.Value} ({action}): {ex.GetType().Name}: {ex.Message}");
            TaskbarLog.Swallowed("TaskItem.Toggle", ex); // helper unavailable — refreshes next poll
        }
    }
}
