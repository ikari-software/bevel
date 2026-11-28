using System.Windows.Input;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bevel.Core;
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
    private readonly TaskButtonClickPolicy _clicks;
    private readonly TaskAttentionPolicy _attention;
    private readonly IAttentionTimer _attentionTimer;
    /// <summary>Optimistic focus claims go through the shell's exclusive projection (bevel-yslj /
    /// bevel-c04q). Null in unit tests that construct a lone VM with no strip.</summary>
    private readonly Action<string?>? _claimFocus;
    /// <summary>bevel-be86: launch hook for a PINNED app button with no window (bundle id in).
    /// Only synthetic pin entries carry one — a helper app-presence entry (running, windowless) still
    /// activates, since there IS something to activate.</summary>
    private readonly Func<string, Task>? _launchApp;
    private string _title = "";
    private bool _isFocused;
    private bool _isMinimized;
    private bool _isFullscreen;
    private bool _isOnOtherSpace;
    private double _width;
    private double _lastLiveWidth;
    private double _opacity;
    private bool _isClosing;
    private Bitmap? _iconSource;
    private bool _showLabel = true;

    /// <param name="clicks">Click semantics for this button (bevel-au94); defaults to the taskbar
    /// process's shared policy, which <c>TaskbarView</c> keeps in step with the settings poll.</param>
    /// <param name="claimFocus">Shell-owned exclusive focus claim (see <c>ShellModel.ClaimFocus</c>).
    /// Optimistic click/minimize must use this — never write <see cref="IsFocused"/> directly when a
    /// strip of siblings exists, or two buttons can stay pressed (bevel-yslj).</param>
    public TaskItemViewModel(ForeignWindow w, IWindowManager windows, TaskButtonClickPolicy? clicks = null,
        Action<string?>? claimFocus = null, Func<string, Task>? launchApp = null,
        TaskAttentionPolicy? attention = null, IAttentionTimer? attentionTimer = null)
    {
        _windows = windows;
        _clicks = clicks ?? TaskButtonClickPolicy.Shared;
        _attention = attention ?? TaskAttentionPolicy.Shared;
        _attentionTimer = attentionTimer ?? new DispatcherAttentionTimer();
        _claimFocus = claimFocus;
        _launchApp = launchApp;
        Id = w.Id;
        AppId = w.AppId;
        IsAppPresence = w.IsAppPresence;
        // Bundle id for app-level actions (Quit). Prefer the explicit field; for a windowless entry the
        // id is "app:<bundle>", so parse it as a fallback.
        BundleId = w.BundleId ?? (w.Id.Value.StartsWith("app:") ? w.Id.Value["app:".Length..] : null);
        BundlePath = w.BundlePath;
        Update(w);
        if (w.IsFocused)
            SetFocused(true);
        ActivateCommand = new AsyncRelayCommand(ToggleAsync);
        CloseCommand = new AsyncRelayCommand(() => _windows.CloseAsync(Id));
        MinimizeCommand = new AsyncRelayCommand(() => _windows.MinimizeAsync(Id));
        RestoreCommand = new AsyncRelayCommand(() => _windows.RestoreAsync(Id));
        ZoomCommand = new AsyncRelayCommand(() => _windows.ZoomAsync(Id));
        QuitCommand = new AsyncRelayCommand(() => TerminateAsync(force: false));
        ForceQuitCommand = new AsyncRelayCommand(() => TerminateAsync(force: true));
        HideCommand = new AsyncRelayCommand(HideAsync);
    }

    private Task TerminateAsync(bool force) =>
        string.IsNullOrEmpty(BundleId) ? Task.CompletedTask : _windows.TerminateAppAsync(BundleId, force);

    private Task HideAsync() =>
        string.IsNullOrEmpty(BundleId) ? Task.CompletedTask : _windows.HideAppAsync(BundleId);

    public ForeignWindowId Id { get; }

    /// <summary>Owning app's identifier (bundle id), used to collapse multi-window apps into a group
    /// (bevel-m2.10.3). Null/empty for windows with no known app — those never group.</summary>
    public string? AppId { get; }

    /// <summary>True for a running-but-windowless app entry (bevel-ww71): rendered dim + icon-only, and a
    /// click just reopens the app (no minimize/toggle, since there's no window).</summary>
    public bool IsAppPresence { get; }

    /// <summary>Owning app's bundle id (stable key), used by <see cref="QuitCommand"/> and pin keying.
    /// Null when unknown. Updatable in place (bevel-be86 review): the platform can attach a window's
    /// bundle id only after the VM was built, so <see cref="Update"/> re-keys it and raises
    /// PropertyChanged, letting the projector re-plan which pin the window covers.</summary>
    public string? BundleId { get => _bundleId; private set => SetProperty(ref _bundleId, value); }
    private string? _bundleId;

    /// <summary>Owning app's bundle path on disk (bevel-4nct), carried for the context menu's
    /// "Show in Finder" verb. Null when the window manager did not know it — the verb is omitted then.</summary>
    public string? BundlePath { get; private set; }

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

    /// <summary>Native fullscreen (own Space) — arrange actions must skip it (bevel-apm). False when
    /// unknown, never guessed.</summary>
    public bool IsFullscreen { get => _isFullscreen; private set => SetProperty(ref _isFullscreen, value); }

    /// <summary>Window's Space is known and not on the current display (bevel-apm arrange filter;
    /// bevel-jt30 projector filter). Carried so neither has to re-ask the PAL; raising
    /// PropertyChanged lets the projector re-plan when a window MOVES spaces. False when unknown.</summary>
    public bool IsOnOtherSpace { get => _isOnOtherSpace; private set => SetProperty(ref _isOnOtherSpace, value); }

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

    /// <summary>Accessible state and the action clicking the button will perform. The active window's hint
    /// follows the reclick policy (bevel-au94), so the button never advertises a minimize this click won't
    /// actually do; the unread badge (bevel-ijln) is appended so a screen reader announces the count the
    /// pill shows visually.</summary>
    public string StatusText
    {
        get
        {
            var state = IsMinimized
                ? $"{Title} — Minimized (click to restore)"
                : IsFocused
                    ? _clicks.MinimizeGestureHint is { } hint ? $"{Title} — Active ({hint})" : $"{Title} — Active"
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

    /// <summary>When true, the view's next layout pass sets this button's first width/opacity with its
    /// transitions suspended, so it appears at full size instead of growing in (bevel-adu8: app-presence
    /// entries popping in after startup). The view clears it once applied.</summary>
    public bool SkipGrowIn { get; set; }

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
    /// <summary>Zoom/maximize this window (bevel-kpkx): the green-button zoom on macOS.</summary>
    public ICommand ZoomCommand { get; }

    /// <summary>Quit the owning app (graceful terminate) — the app-level action every button offers.</summary>
    public ICommand QuitCommand { get; }

    /// <summary>Force-quit the owning app (Alt/Option in the menu).</summary>
    public ICommand ForceQuitCommand { get; }

    /// <summary>Hide the whole owning app (bevel-d2j9) — NSRunningApplication.hide() on macOS; the app
    /// keeps running with no visible windows. App-level like Quit: keyed on <see cref="BundleId"/>.</summary>
    public ICommand HideCommand { get; }

    /// <summary>Refreshes the label/focus/minimized state from a fresh window snapshot in place,
    /// keeping the same VM object so the list row and its icon survive the update.</summary>
    public void Update(ForeignWindow w)
    {
        Title = string.IsNullOrEmpty(w.Title) ? w.AppId ?? "" : w.Title;
        IsMinimized = w.IsMinimized;
        IsFullscreen = w.IsFullscreen;
        IsOnOtherSpace = w.IsOnOtherSpace;
        BundlePath = w.BundlePath;
        // bevel-be86 review: re-key when the snapshot finally carries a bundle id (learned late) —
        // the projector re-plans on the change, so a pin covered by this window heals.
        if (w.BundleId is { Length: > 0 } b) BundleId = b;
        // IsFocused is owned by ShellModel.ApplyExclusiveFocus — per-event isFocused flags
        // go stale and were leaving multiple buttons in the pressed state.
        // Attention (bevel-qb19): only a STRICTLY NEWER non-zero stamp is an event (the helper
        // suppresses re-stamps within its min-re-trigger interval, and the 500ms poll re-sends the
        // sticky value — both must be no-ops), unless this window is already focused (activation is
        // the acknowledgement). App-presence (windowless) entries never animate: the icon-only
        // button must not pulse.
        if (!IsAppPresence && w.AttentionAt > 0 && w.AttentionAt > _attentionAt)
        {
            _attentionAt = w.AttentionAt;
            if (!IsFocused)
                StartAttention();
        }
    }

    /// <summary>Sets focus from the shell's exclusive foreground projection.</summary>
    public void SetFocused(bool focused)
    {
        IsFocused = focused;
        // bevel-qb19: activating/focusing the window acknowledges its attention request.
        if (focused)
            StopAttention();
    }

    // ── Attention state (bevel-qb19) ─────────────────────────────────────
    //
    // The helper stamps attention_at when a window NEWLY APPEARS or its TITLE CHANGES while its app
    // is NOT foreground (never inferred from title/badge CONTENT). The stamp flows WindowChange ->
    // ForeignWindow.AttentionAt -> here. Active animates; bounded settles into a static tint after
    // MaxPulses cycles; until-activated runs until activation/close.

    private long _attentionAt;
    private long _attentionFirstStamp;   // first stamp of the current Active run — the settle deadline anchors here
    private bool _settleArmed;           // a bounded settle timer is pending (don't re-arm while armed)
    private TaskAttentionState _attentionState;

    /// <summary>Raw attention stamp (unix ms, 0 = none) exactly as the helper last reported it.</summary>
    public long AttentionAt => _attentionAt;

    /// <summary>Attention state machine: Idle → Active → (bounded) Settled, back to Idle on
    /// activation/close.</summary>
    public TaskAttentionState AttentionState
    {
        get => _attentionState;
        private set
        {
            if (!SetProperty(ref _attentionState, value)) return;
            OnPropertyChanged(nameof(AttentionVisual));
        }
    }

    /// <summary>The overlay visual, resolved live from the state + the shared policy (settings and
    /// reduced-motion can change it under a running animation).</summary>
    public TaskAttentionVisual AttentionVisual => TaskAttentionPolicy.ResolveVisual(_attentionState, _attention);

    private void StartAttention()
    {
        var wasActive = AttentionState == TaskAttentionState.Active;
        if (!wasActive)
            _attentionFirstStamp = _attentionAt;   // the settle deadline anchors at the FIRST stamp
        AttentionState = TaskAttentionState.Active;
        // Bounded mode arms the settle timer; until-activated (and the static modes, which resolve
        // to a tint immediately) run no timer and stop only on activation/close.
        if (_attention.EffectiveDuration == TaskbarAttentionDuration.Bounded &&
            _attention.EffectiveAnimation is TaskbarAttentionAnimation.Pulse or TaskbarAttentionAnimation.Sweep)
        {
            if (!wasActive)
            {
                _attentionTimer.Start(TaskAttentionPolicy.BoundedSettleDelay, SettleAttention);
                _settleArmed = true;
            }
            else if (!_settleArmed)
            {
                // The policy flipped into bounded mid-run (settings live-apply): arm with the
                // REMAINING budget measured from the first stamp, not a fresh full delay.
                _attentionTimer.Start(RemainingSettleDelay(), SettleAttention);
                _settleArmed = true;
            }
            // Already Active with the settle timer pending: a newer stamp NEVER re-arms it —
            // the deadline stays firstStamp + BoundedSettleDelay, so a title-chatty background
            // app still settles instead of pulsing forever.
        }
        else
        {
            _attentionTimer.Stop();
            _settleArmed = false;
        }
    }

    /// <summary>The bounded budget left, measured from the first stamp of this Active run
    /// (both are helper unix-ms stamps on the same machine clock). Never negative.</summary>
    private TimeSpan RemainingSettleDelay()
    {
        var elapsedMs = Math.Max(0, _attentionAt - _attentionFirstStamp);
        var remaining = TaskAttentionPolicy.BoundedSettleDelay - TimeSpan.FromMilliseconds(elapsedMs);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void SettleAttention()
    {
        _settleArmed = false;
        if (AttentionState == TaskAttentionState.Active)
            AttentionState = TaskAttentionState.Settled;
    }

    /// <summary>Stops the attention animation (window activated, closed, app quit, button clicked).
    /// Idempotent.</summary>
    public void StopAttention()
    {
        _attentionTimer.Stop();
        _settleArmed = false;
        AttentionState = TaskAttentionState.Idle;
    }

    /// <summary>Re-resolves the visual after the shared policy changed (settings apply / reduced-motion
    /// probe) and re-arms the settle timer if a bounded animation is still running under new semantics.</summary>
    internal void RefreshAttentionVisual()
    {
        if (AttentionState == TaskAttentionState.Active)
            StartAttention();
        // Always raise: the state may be unchanged while the resolved visual moved (mode/reduced-motion).
        OnPropertyChanged(nameof(AttentionVisual));
    }

    /// <summary>Optimistic focus edit: prefer the shell claim so exclusivity holds; fall back to a
    /// direct write only when this VM was built without a strip (lone-unit tests).</summary>
    private void ClaimFocus(string? focusedId)
    {
        if (_claimFocus is not null)
            _claimFocus(focusedId);
        else
            IsFocused = focusedId is not null && focusedId == Id.Value;
    }

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
    ///   focused   → minimize, when the reclick policy says this gesture minimizes (bevel-au94);
    ///   otherwise → activate.
    /// The focused arm is the only configurable one: with minimize turned off — or moved to Option/Alt —
    /// a plain click on the active window falls through to activate, a harmless raise, so a click is
    /// never a no-op.
    /// </summary>
    private async Task ToggleAsync()
    {
        // App-presence button (bevel-ww71): no window to minimize/toggle — a click just reopens the app.
        // The helper's activateWindow resolves the "app:<bundle>" id to NSRunningApplication.activate().
        if (IsAppPresence)
        {
            // bevel-be86: a PINNED app with no window is (almost certainly) not running, so there is
            // nothing to activate — launch it instead (IAppEnvironment.LaunchAsync resolves the bundle
            // id and also re-activates an already-running app). Helper presence entries carry no launch
            // hook and keep the activate path.
            if (_launchApp is not null && BundleId is { Length: > 0 } bundle)
            {
                TaskbarLog.Debug($"CLICK id={Id.Value} pinned -> launch {bundle}");
                try { await _launchApp(bundle); }
                catch (Exception ex) { TaskbarLog.Swallowed("PinnedApp.Launch", ex); }
                return;
            }
            TaskbarLog.Debug($"CLICK id={Id.Value} app-presence -> reopen");
            try { await _windows.ActivateAsync(Id); }
            catch (Exception ex) { TaskbarLog.Debug($"CLICK app-presence failed id={Id.Value}: {ex.Message}"); }
            return;
        }

        // Decided once, before any optimistic state edit: the modifier probe reads live hardware state, so
        // re-asking it per branch could get a different answer part-way through one click.
        var minimize = !IsMinimized && IsFocused && _clicks.MinimizesFocusedWindow();
        var action = IsMinimized ? "restore+activate" : minimize ? "minimize" : "activate";
        TaskbarLog.Debug($"CLICK id={Id.Value} title='{Title}' min={IsMinimized} focus={IsFocused} " +
            $"reclick={_clicks.Mode} -> {action}");
        try
        {
            if (IsMinimized)
            {
                IsMinimized = false;   // optimistic: the button un-dims on the click, not a round-trip later
                // ONE atomic op (bevel-nxic): the helper de-miniaturizes then raises the window LAST, so a
                // second RPC no longer races the de-miniaturize animation and lands the window mid-stack.
                await _windows.RestoreAndActivateAsync(Id);
            }
            else if (minimize)
            {
                IsMinimized = true;
                // Optimistic: un-press immediately via the exclusive projection (null = nothing pressed).
                ClaimFocus(null);
                await _windows.MinimizeAsync(Id);
            }
            else
            {
                // Optimistic pressed state (bevel-c04q): don't wait on the Activate RPC to light the
                // button — but claim through ShellModel so siblings clear (bevel-yslj).
                ClaimFocus(Id.Value);
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
