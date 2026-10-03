using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Core.Components;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar.Components;

namespace Bevel.Taskbar;

public partial class TaskbarView : UserControl
{
    private StartMenu? _startMenu;
    private IAppEnvironment? _appEnv;
    private IIconProvider? _iconProvider;
    private ITabProvider? _tabProvider;
    private Action? _quit;
    private Action? _restart;
    /// Targeted repair for a lost core link: respawn only the shell-core process (the launcher owns it).
    /// Null when unsupervised — the repair menu then offers only the whole-shell restart.
    private Action? _restartCore;
    private Action? _openSettings;
    private Action<Bevel.Core.Vfs.VfsPath>? _openFolder;
    private Action? _openSearch;
    private Action? _toggleDesktop;
    private Func<bool?>? _desktopRunning;
    private Action? _toggleLock;
    private int _maxButtonWidth = 160;
    private int _minButtonWidth = 80;
    private TaskbarButtonWidthMode _widthMode = TaskbarButtonWidthMode.ShrinkToFit;
    private TaskbarButtonSize _buttonSize = TaskbarButtonSize.Normal;
    private TaskbarGroupingMode _grouping = TaskbarGroupingMode.Never;
    private TaskbarWindowSort _sort = TaskbarWindowSort.OpenOrder;
    private bool _windowlessLast;
    private TaskbarButtonLabels _buttonLabels = TaskbarButtonLabels.Auto;
    private bool _middleClickCloses = true;
    private int _trayOverflowCap = 8;
    private int _trayIconSize = 16;
    private bool _locked;
    private bool _alwaysOnTop = true;
    private bool _showDesktop;
    private int _fontSize;
    /// <summary>Start badge edge from the theme's <c>Bevel.Metric.StartBadgeSize</c> (live via resource observable).</summary>
    private double _startBadgeSize = StartLogo.DefaultSize;
    /// <summary>User option: the full glass-and-core mark even at small size (Appearance tab).</summary>
    private bool _startBadgeFullDetail;
    private string _bgColor = "";
    private int _opacity = 100;
    private TaskbarWindow? _window;
    private TaskbarViewModel? _vm;
    /// <summary>Composes the stacks region from <see cref="BevelSettings.TaskbarComponents"/> (bevel-aqr7
    /// Task 14). Null until <see cref="InitComponentRegion"/> runs from <see cref="OnLoaded"/>.</summary>
    private ComponentBarHost? _componentHost;
    /// <summary>
    /// Test-only seam (bevel-aqr7 Task 14 fix round 2, Finding 1): lets <c>Bevel.Taskbar.Tests</c>
    /// (via the existing <c>InternalsVisibleTo</c>) drive <see cref="ApplyComponentRegion"/> directly
    /// and inspect the REAL resulting slots, rather than a test re-implementing the type filter
    /// inline and never exercising the production call site at all.
    /// </summary>
    internal ComponentBarHost? ComponentHost => _componentHost;
    /// <summary>
    /// The settings snapshot <see cref="Initialize"/> was called with, held only long enough to seed
    /// <see cref="InitComponentRegion"/> from <see cref="OnLoaded"/>. It cannot run at Initialize-time
    /// itself: <c>Initialize</c> is called BEFORE this view is parented into its owning
    /// <c>TaskbarWindow</c> (see <c>App.axaml.cs</c>'s <c>CreateTaskbarSurfaceCore</c>), so
    /// <see cref="_window"/> — and the real <c>BarGeometry</c> the host needs — isn't set until
    /// <c>OnLoaded</c> runs, exactly like the other attach-dependent seeding (<c>ApplyAppearance</c>)
    /// just below it.
    /// </summary>
    private Bevel.Core.BevelSettings? _initialSettings;
    private bool _resizing;
    private DispatcherTimer? _tooltipTimer;
    private Control? _tooltipAnchor;
    private int _previewGeneration;   // tags each hover-preview capture so a superseded/late one is discarded
    private bool _wired;
    private bool _layoutQueued;

    // Menu-scoped key focus (bevel-vk4n): the taskbar becomes key ONLY while a menu/popover it owns is
    // open. A depth counter lets nested/overlapping menus (Start menu + a task menu) coexist without one
    // closing dropping key focus the other still needs. Flyouts are wired once each (deduped).
    private int _menuScopeDepth;
    private readonly HashSet<Avalonia.Controls.Primitives.FlyoutBase> _scopedFlyouts = new();
    private IntPtr _startHotkeyMonitor;   // native global Ctrl+Esc / Option+Esc monitor token (macOS)

    public TaskbarView()
    {
        InitializeComponent();
        ApplyTaskIconMetric();   // seed the glyph token from the tier App already Configure()d
    }

    /// <summary>
    /// Republishes <see cref="TaskbarTheme.TaskIconSize"/> as the <c>Bevel.Metric.TaskbarIconSize</c>
    /// resource the task-button templates bind their Image Width/Height to (bevel-c54t). Assigning the
    /// resource is what makes a live tier change re-size already-realized buttons: the templates use
    /// DynamicResource, so every bound Image (and anything else keyed off the token, e.g. a badge)
    /// re-measures without rebuilding the strip. Cheap, UI-thread-only, no icon work.
    /// </summary>
    private void ApplyTaskIconMetric() =>
        Resources["Bevel.Metric.TaskbarIconSize"] = (double)TaskbarTheme.TaskIconSize;

    public Button StartButtonControl => StartButton;
    public ItemsControl WindowButtonAreaControl => WindowButtonArea;
    public ClockWidget ClockControl => Clock;

    /// <summary>
    /// Supplies the Start menu's app environment + icon provider and the max button width. The
    /// window-button list itself now comes from the bound <see cref="TaskbarViewModel"/>
    /// (DataContext) via the background <see cref="ShellModel"/> — the taskbar no longer
    /// subscribes to window events or builds/mutates buttons by hand (bevel-d2z).
    /// </summary>
    /// <summary>
    /// Click on the pulsing disconnected indicator — opens the repair menu (bevel-corepulse). The
    /// indicator is the only affordance the user has while the core is unreachable: the Start menu's
    /// own restart path routes through shell state that may itself be stale, so the repairs are offered
    /// right here, on the thing that is already demanding attention.
    /// </summary>
    private void OnDisconnectedIndicatorPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control)
            FlyoutBase.ShowAttachedFlyout(control);
        e.Handled = true;
    }

    /// <summary>Respawn just the shell core — the targeted fix, and the one that repairs an unlinked
    /// core socket without disturbing open Filer windows.</summary>
    private void OnRepairRestartCore(object? sender, RoutedEventArgs e) => _restartCore?.Invoke();

    /// <summary>Restart every shell process — the heavier fallback when a core respawn doesn't take.</summary>
    private void OnRepairRestartShell(object? sender, RoutedEventArgs e) => _restart?.Invoke();

    public void Initialize(
        Bevel.Core.BevelSettings settings,
        IAppEnvironment? appEnv = null,
        IIconProvider? iconProvider = null,
        Action? quit = null,
        Action? restart = null,
        Action? restartCore = null,
        Action? openSettings = null,
        Action? toggleLock = null,
        Action<Bevel.Core.Vfs.VfsPath>? openFolder = null,
        Action? openSearch = null,
        Action? toggleDesktop = null,
        Func<bool?>? desktopRunning = null,
        ITabProvider? tabProvider = null,
        Bevel.Core.ISettingsService? settingsService = null)
    {
        // Non-settings wiring (PAL services + the shell-command callbacks).
        _appEnv = appEnv;
        _iconProvider = iconProvider;
        _tabProvider = tabProvider;
        _quit = quit;
        _restart = restart;
        _restartCore = restartCore;
        // Only offer a repair that can actually run: an unsupervised taskbar has no launcher to ask.
        if (RepairRestartCoreItem is not null) RepairRestartCoreItem.IsEnabled = restartCore is not null;
        if (RepairRestartShellItem is not null) RepairRestartShellItem.IsEnabled = restart is not null;
        _openSettings = openSettings;
        _openFolder = openFolder;
        _openSearch = openSearch;
        _toggleDesktop = toggleDesktop;
        _desktopRunning = desktopRunning;
        _toggleLock = toggleLock;

        // Every persisted setting flows from the one BevelSettings (bevel-ccs) — no more 25-param call.
        // These mirror ApplyLiveSettings; startup can't diverge from a live change because both read the
        // same fields. (Appearance is deferred to OnLoaded, since it reads theme resources that don't
        // resolve until the view is attached — so it's stored here, not applied.)
        _maxButtonWidth = settings.TaskbarButtonWidth;
        _widthMode = settings.TaskbarButtonWidthMode;
        _buttonSize = settings.TaskbarButtonSize;
        _minButtonWidth = Math.Clamp(settings.TaskbarMinButtonWidth, IconOnlyFloorFor(TaskbarTheme.TaskIconSize), settings.TaskbarButtonWidth);
        _grouping = settings.TaskbarGrouping;
        _buttonLabels = settings.TaskbarButtonLabels;
        _middleClickCloses = settings.TaskbarMiddleClickCloses;
        // Click semantics live in the shared policy, not a field here: buttons created later by ShellModel
        // (which has no settings) read the same object, so a live change reaches them too (bevel-au94).
        TaskButtonClickPolicy.Shared.Mode = settings.TaskbarReclickMinimize;
        _sort = settings.TaskbarWindowSort;
        _windowlessLast = settings.WindowlessAppsLast;
        _trayOverflowCap = settings.TaskbarTrayOverflowCap;
        _trayIconSize = settings.TaskbarTrayIconSize;
        _locked = settings.TaskbarLocked;
        _alwaysOnTop = settings.TaskbarAlwaysOnTop;
        _showDesktop = settings.TaskbarShowDesktopButton;
        Clock.Configure(settings.TaskbarShowClock, settings.TaskbarClock24Hour, settings.TaskbarClockShowSeconds, settings.TaskbarClockShowDate);
        ApplyStart(settings.TaskbarShowStart, settings.TaskbarStartLabel);
        _startBadgeFullDetail = settings.StartBadgeFullDetail;   // the OnLoaded size observable does the first build
        _fontSize = settings.TaskbarFontSize;
        _bgColor = settings.TaskbarBackgroundColor;
        _opacity = settings.TaskbarOpacity;

        // Composes the stacks region from the component list (bevel-aqr7 Task 14). Stored rather than
        // applied here: InitComponentRegion needs _window (not yet set — see _initialSettings's doc),
        // so it runs from OnLoaded, mirroring every other field this method seeds for later use there.
        _initialSettings = settings;

        // Peer-settings arrival race (bevel-kclq regression, 2026-09-29): a split-process taskbar's
        // ISettingsService (RemoteSettingsService) can paint its FIRST snapshot — the one every field
        // above was just read from — before the persisted values actually land (a stale/cold on-disk
        // cache, or a core connect that is still in flight). When the real snapshot then arrives, it
        // fires only ISettingsService.Changed; nothing upstream (App.OnFrameworkInitializationCompleted's
        // generic Changed handler applies theme/folder-options only) was pushing a LATE correction into
        // an already-built TaskbarWindow/TaskbarView. Concretely: TaskbarWindow gets constructed with the
        // stale TaskbarRows, OnLoaded computes StartButton.MaxHeight from that stale row count, and the
        // Start menu (anchored above the button) stays parked at the WRONG row's height for the rest of
        // the session — restarting is the only thing that picks up a by-then-warm cache. Wiring the
        // correction here — the same call ApplyLiveSettings already gets from the two LOCAL apply paths
        // (ToggleTaskbarLock, the Properties dialog) — closes the race generally, for every field this
        // method seeds, not just rows. Idempotent: TaskbarWindow.SetRows is a no-op when unchanged.
        _settingsService = settingsService;
        if (_settingsService is not null && !_settingsServiceWired)
        {
            _settingsServiceWired = true;
            _settingsService.Changed += OnSettingsServiceChanged;
        }
    }

    private Bevel.Core.ISettingsService? _settingsService;
    private bool _settingsServiceWired;

    /// <summary>
    /// Builds the registry + <see cref="ComponentBarHost"/> that compose the stacks region from
    /// <see cref="BevelSettings.TaskbarComponents"/> (bevel-aqr7 Task 14). Only the Stack type is
    /// registered so far — Start/window-strip/tray/clock keep their current hand-built layout and
    /// migrate in a follow-on task (spec §1 blesses incremental migration). The host's own panel
    /// becomes <see cref="ComponentRegionHost"/>'s content once, here; later settings changes only
    /// call <see cref="ApplyComponentRegion"/>, never rebuild the host.
    /// </summary>
    private void InitComponentRegion(Bevel.Core.BevelSettings settings)
    {
        var caps = new HashSet<string>(StringComparer.Ordinal);   // PAL capability names; empty until wired
        var registry = new ComponentRegistry(caps);
        registry.Register(StackComponentManifest.Create(), inst => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId,
                new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                false)));

        _componentHost = new ComponentBarHost(registry, new ComponentHealth(), _window!.Geometry);
        ComponentRegionHost.Content = _componentHost.View;
        ApplyComponentRegion(settings);
    }

    /// <summary>
    /// Re-applies the persisted component list to the running bar. Safe to call on every settings
    /// change: <see cref="ComponentBarHost.ApplyAsync"/> replaces slots rather than accumulating them.
    /// Runs off the UI thread — <see cref="IComponentChannel.ConnectAsync"/> may touch IPC — and
    /// <c>ApplyAsync</c> marshals only its own panel mutation back via <see cref="Dispatcher"/>, so
    /// this never blocks the caller (settings-changed handlers run on the UI thread).
    ///
    /// <c>internal</c> rather than <c>private</c> (bevel-aqr7 Task 14 fix round 2, Finding 1) so
    /// <c>ComponentRegionFilterTests</c> can call the REAL method with a full migrated list and
    /// prove the type filter below actually guards the stacks region — a test that filtered the
    /// list itself before calling <see cref="ComponentBarHost.ApplyAsync"/> would still pass even if
    /// this method's filter were deleted, which is exactly how the ghost-placeholder Critical got
    /// through undetected in the first place.
    /// </summary>
    internal void ApplyComponentRegion(Bevel.Core.BevelSettings settings)
    {
        var host = _componentHost;
        if (host is null) return;

        // FILTER to the types this region's registry actually serves. The migration populates
        // TaskbarComponents with Start, WindowStrip, Stack, Tray, Clock AND ShowDesktop, but this
        // task registers only Stack — so handing over the whole list makes the normalizer keep every
        // other type as an "unknown" inert placeholder and renders ghost 12x12 blanks inside the
        // stacks region, right next to the real hand-rendered Start button, strip, tray and clock.
        // That reproduces on essentially every real settings.db. Widen this filter as each region
        // migrates; delete it when all of them have.
        var list = settings.TaskbarComponents
            .Where(i => i.TypeId == TaskbarComponentTypes.Stack)
            .ToArray();
        // Off the UI thread: ConnectAsync may touch IPC. Only the panel mutation marshals back,
        // which ApplyAsync already does for itself.
        _ = Task.Run(() => host.ApplyAsync(list, CancellationToken.None));
    }

    /// <summary>Fired off the shell-core transport thread (or synchronously for an in-process fake) —
    /// marshal to the UI thread before touching any control, exactly like App's own settings.Changed
    /// handler does.</summary>
    private void OnSettingsServiceChanged()
    {
        var service = _settingsService;
        if (service is null) return;
        Dispatcher.UIThread.Post(() => ApplyLiveSettings(service.Current));
    }

    /// <summary>Sets the Start button's visibility and caption (empty caption = logo only).</summary>
    /// <summary>Rebuilds the Start badge from the current theme size + the user's detail option. Cheap (a
    /// handful of vector paths), so theme switches and the Appearance checkbox just call it.</summary>
    private void RebuildStartBadge()
        => StartLogoHost.Content = StartLogo.For(_startBadgeSize, _startBadgeFullDetail);

    private void ApplyStart(bool show, string label)
    {
        StartButton.IsVisible = show;
        StartLabelText.Text = label;
        StartLabelText.IsVisible = !string.IsNullOrEmpty(label);
        // A logo-only button still needs a spoken name, so the caption can't be the only source
        // (an empty AutomationProperties.Name reads as an unlabelled button).
        AutomationProperties.SetName(StartButton, string.IsNullOrEmpty(label) ? "Start menu" : label);
        SyncStartDivider();
    }

    /// <summary>
    /// Keeps the divider glued to the Start button's right edge. The button auto-sizes to its caption
    /// (bevel-6x9z), so the old hardcoded <c>Margin="76,1,0,1"</c> — which encoded the former fixed
    /// 74px button plus its margin — detaches the moment the caption changes. Driven from the button's
    /// live <c>Bounds</c> rather than from the label text, because the real width also depends on the
    /// badge size, padding and the theme's minimum.
    /// </summary>
    private void SyncStartDivider()
    {
        if (StartDivider is null || StartButton is null) return;

        // Nothing to divide when the button is hidden: the strip starts at the bar's left edge.
        StartDivider.IsVisible = StartButton.IsVisible;
        if (!StartButton.IsVisible) return;

        var margin = StartButton.Margin;
        StartDivider.Margin = new Thickness(
            StartButton.Bounds.Width + margin.Left + margin.Right, 1, 0, 1);
    }

    /// <summary>Applies font size + background tint/opacity to the running bar (bevel-cust.appearance).
    /// Font size falls back to (and is clamped around) the 11pt theme baseline. On reset (opaque, no
    /// tint) it CLEARS the local Background so the XAML DynamicResource re-applies — the bar keeps
    /// following later theme switches, unlike caching a concrete brush (review: correctness/adversarial).
    /// Reads the theme colour live rather than caching it, so it must run attached (called from OnLoaded
    /// and ApplyLiveSettings, never pre-attach where resource lookup fails).</summary>
    private void ApplyAppearance(int fontSize, string bgColor, int opacity)
    {
        FontSize = fontSize > 0 ? Math.Clamp(fontSize, 8, 32) : 11;

        // Opacity is the ALPHA of the taskbar fill (RGBA/HSVA): scale the whole themed background LAYER's
        // alpha uniformly, so the theme's real fill — Luna's purple GRADIENT, Win2000's grey — is kept
        // (never flattened to the button-face colour) and the desktop shows through it (bevel-cust.appearance).
        TaskbarBg.Opacity = FillOpacity(opacity);

        if (!string.IsNullOrWhiteSpace(bgColor) && Avalonia.Media.Color.TryParse(bgColor, out var custom))
            // A user-picked flat colour overrides the theme fill (its alpha still comes from Opacity above).
            TaskbarBg.Background = new Avalonia.Media.SolidColorBrush(custom);
        else
            // Re-establish the theme-token binding — NOT ClearValue. The fill is set in XAML as
            // {DynamicResource Bevel.Brush.TaskbarBackground}; ClearValue would drop the DynamicResource
            // subscription and freeze the fill on the current token, blind to later theme swaps. Re-applying
            // the DynamicResource keeps the layer live-tracking the token across theme changes (bevel-dob).
            TaskbarBg[!Avalonia.Controls.Border.BackgroundProperty] =
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Bevel.Brush.TaskbarBackground");
    }

    /// <summary>The taskbar fill's alpha as a 0–1 opacity (bevel-cust.appearance). Pure + testable.
    /// The stored percentage is clamped to 20–100 so the bar never vanishes entirely.</summary>
    internal static double FillOpacity(int opacity) => Math.Clamp(opacity, 20, 100) / 100.0;

    /// <summary>Pushes every live-applicable setting onto the running taskbar in one shot (clock, Start,
    /// grouping, label mode). Called by the Properties dialog's ApplyLive hook — same process, so the
    /// change is visible immediately without a restart.</summary>
    public void ApplyLiveSettings(Bevel.Core.BevelSettings s)
    {
        // Keep the OnLoaded seed current too: a settings snapshot can legitimately arrive before
        // OnLoaded ever runs (the same peer-settings race _settingsServiceWired's comment describes),
        // and InitComponentRegion must not build the host from a stale snapshot once it does.
        _initialSettings = s;
        Clock.Configure(s.TaskbarShowClock, s.TaskbarClock24Hour, s.TaskbarClockShowSeconds, s.TaskbarClockShowDate);
        ApplyStart(s.TaskbarShowStart, s.TaskbarStartLabel);
        if (_startBadgeFullDetail != s.StartBadgeFullDetail)
        {
            _startBadgeFullDetail = s.StartBadgeFullDetail;
            RebuildStartBadge();
        }
        _grouping = s.TaskbarGrouping;
        _vm?.SetGrouping(_grouping);
        _sort = s.TaskbarWindowSort;
        _windowlessLast = s.WindowlessAppsLast;
        _vm?.SetSort(_sort, _windowlessLast);
        _buttonLabels = s.TaskbarButtonLabels;
        _middleClickCloses = s.TaskbarMiddleClickCloses;
        TaskButtonClickPolicy.Shared.Mode = s.TaskbarReclickMinimize;   // bevel-au94: live, no restart
        _fontSize = s.TaskbarFontSize;
        _bgColor = s.TaskbarBackgroundColor;
        _opacity = s.TaskbarOpacity;
        ApplyAppearance(_fontSize, _bgColor, _opacity);
        _vm?.Tray.Configure(s.TaskbarTrayOverflowCap, s.TaskbarTrayIconSize);
        _vm?.Tray.SetConsolidated(s.TaskbarConsolidateMenuBar);   // Strategy C (bevel-7hf4): in-process apply path
        ApplyComponentRegion(s);   // bevel-aqr7 Task 14: re-apply the component list, off the UI thread

        _locked = s.TaskbarLocked;
        ResizeGrip.IsVisible = !_locked;
        LockMenuItem.IsChecked = _locked;   // reflect lock state as the context-menu check mark
        // Only re-issue the native window-level set when it actually changed — every dialog toggle
        // funnels through here, and re-stacking the NSWindow on unrelated changes can flicker z-order.
        if (_alwaysOnTop != s.TaskbarAlwaysOnTop)
        {
            _alwaysOnTop = s.TaskbarAlwaysOnTop;
            _window?.SetAlwaysOnTop(_alwaysOnTop);
        }
        _showDesktop = s.TaskbarShowDesktopButton;
        ShowDesktopButton.IsVisible = _showDesktop;

        // Button width/size — apply live, no restart (bevel-cust). Width settings are cheap (they only
        // feed the next LayoutButtons pass). Button SIZE changes the bar height, so it's guarded to a
        // real change: it re-Configures the process-wide metric, resizes/re-anchors the window + work-
        // area band, and re-heights the already-realized buttons (WireTaskButton only sets new ones).
        _maxButtonWidth = s.TaskbarButtonWidth;
        _widthMode = s.TaskbarButtonWidthMode;
        _minButtonWidth = Math.Clamp(s.TaskbarMinButtonWidth, IconOnlyFloorFor(TaskbarTheme.TaskIconSize), s.TaskbarButtonWidth);
        if (_buttonSize != s.TaskbarButtonSize)
        {
            _buttonSize = s.TaskbarButtonSize;
            TaskbarTheme.Configure(_buttonSize);
            _window?.ReapplyMetrics();     // resize + re-anchor + refresh band; raises RowsChanged → ApplyRowLayout
            StartButton.MaxHeight = StartButtonMaxHeight();
            ReapplyButtonHeights();
            ApplyTaskIconMetric();         // bigger/smaller glyphs follow the tier, live (bevel-c54t)
        }
        // Row count from the settings slider — the same knob as dragging the resize grip. SetRows is a
        // no-op when unchanged and clamps to the screen-derived MaxRows.
        if (_window != null && _window.Rows != s.TaskbarRows)
            _window.SetRows(s.TaskbarRows);
        LayoutButtons();
    }

    /// <summary>
    /// Traces where the Start menu's popup actually landed against the bar it was anchored to
    /// (bevel-kclq: the menu can open a whole row-height above the taskbar).
    ///
    /// Kept because that bug is STATEFUL, not static — the geometry measures correct at steady state
    /// in both a headless repro and a live session, and a restart clears it. It only appears after the
    /// bar's geometry changes under a menu that has already been built, so catching it needs the
    /// numbers at the moment of opening, in the session where it went wrong. Debug-gated, so it costs
    /// nothing unless BEVEL_DEBUG_TASKBAR is set. Remove once the placement is fixed and covered.
    /// </summary>
    private void TraceMenuPlacement()
    {
        if (!TaskbarLog.IsEnabled || _startMenu is null) return;
        var host = _startMenu.MenuPopupControl.Host as Avalonia.Controls.Primitives.PopupRoot;
        TaskbarLog.Debug(
            $"kclq placement popupTop={(host is null ? null : (Point?)host.PointToScreen(default).ToPoint(1.0))} " +
            $"popupSize={host?.Bounds.Size} startBtnTop={StartButton.PointToScreen(default).ToPoint(1.0)} " +
            $"barH={_window?.Height} rows={_window?.Rows} heightForRows={TaskbarTheme.HeightForRows(_window?.Rows ?? 1)} " +
            $"band={_window?.GetWorkAreaBand()} scaling={_window?.RenderScaling}");
    }

    /// <summary>
    /// The Start button's height cap: it grows with the bar, but never past <see cref="StartMaxRows"/>
    /// rows — and never past the bar it actually sits in.
    ///
    /// The second clamp is the bevel-kclq fix. This used to be a flat HeightForRows(StartMaxRows), i.e.
    /// 86 for a 3-row cap, which looks harmless on a 2-row bar because 86 &gt; 58 so the cap never binds
    /// on HEIGHT. It binds on PLACEMENT: the Start menu's popup is anchored above the Start button, so a
    /// button the layout treats as 86 tall puts the anchor at the 3-row position and the menu floats one
    /// RowHeight above the bar. Measured on device at rows=2: the popup's bottom landed at y=1355, which
    /// is exactly HeightForRows(3)=86 off the screen bottom plus the Win2000 skin's 1px button margin,
    /// where the bar's own top is 1383.
    /// </summary>
    private double StartButtonMaxHeight()
        => TaskbarTheme.HeightForRows(Math.Min(_window?.Rows ?? 1, StartMaxRows));

    /// <summary>Re-applies the current button-height tier to every realized window button (bevel-cust).
    /// <see cref="WireTaskButton"/> only sets Height when a container is first realized, so a live size
    /// change needs this sweep over the existing buttons.</summary>
    private void ReapplyButtonHeights()
    {
        foreach (var container in WindowButtonArea.GetRealizedContainers())
            if (FindTaskButton(container) is { } button)
                button.Height = TaskbarTheme.ButtonHeight;
    }

    /// <summary>Middle-click a window button to close that window (bevel-cust.buttons). Tunnels so it
    /// fires before the button's own click handling; only single-window buttons act (groups are left
    /// alone). No-op when the setting is off or the press isn't the middle button.</summary>
    private void OnWindowButtonMiddleClick(object? sender, PointerPressedEventArgs e)
    {
        if (!_middleClickCloses) return;
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
        // Guard against IsClosing so a rapid double middle-click (or middle-button auto-repeat) can't
        // fire CloseAsync twice on the same — possibly already-recycled — window id (review: adversarial).
        if ((e.Source as Control)?.DataContext is TaskItemViewModel item && !item.IsClosing)
        {
            item.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Grouped-app flyout row picked: activate the window, then close the flyout. The close is
    /// deferred so activation is fully underway first (hiding the popup mid-gesture would cancel it).
    /// A Border+Tapped (not a Button+Command) so the row shares the flyoutrow hover-highlight with the
    /// stack flyout — one style-driven hover mechanism, not a per-list re-implementation (bevel-cust).</summary>
    private void OnGroupWindowTapped(object? sender, TappedEventArgs e) => ActivateGroupWindowRow(sender);

    /// <summary>Keyboard operability for a grouped-window flyout row (bevel-vk4n): Enter/Space activates
    /// the window, mirroring the row's Tapped gesture so a keyboard/AT user can pick a window.</summary>
    private void OnGroupWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        ActivateGroupWindowRow(sender);
        e.Handled = true;
    }

    private void ActivateGroupWindowRow(object? sender)
    {
        if ((sender as Control)?.DataContext is TaskItemViewModel vm)
        {
            vm.ActivateCommand.Execute(null);
            // This flyout's Closed will fire ExitMenuScope → the key-focus handback; cancel it so we don't
            // re-raise the previously-frontmost app back OVER the window we just activated (bevel-nxic).
            _window?.CancelKeyFocusHandback();
        }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var toggle in WindowButtonArea.GetVisualDescendants().OfType<ToggleButton>())
                toggle.Flyout?.Hide();
        });
    }

    /// <summary>Taskbar right-click → Task Manager: opens macOS Activity Monitor (bevel-cust.ctxmenu).</summary>
    private void OnTaskManagerClick(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "-a \"Activity Monitor\"",
                UseShellExecute = true,
            });
    }

    private void OnLockTaskbarClick(object? sender, RoutedEventArgs e) => _toggleLock?.Invoke();

    private void OnPropertiesClick(object? sender, RoutedEventArgs e) => _openSettings?.Invoke();

    // Stack-flyout cell gestures (click-to-open, keyboard activation, drag-out) moved to
    // StackFlyoutView with the grid markup they belong to (bevel-9elh).

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _vm = DataContext as TaskbarViewModel;
        _window = TopLevel.GetTopLevel(this) as TaskbarWindow;

        // Apply the grouping mode before the first layout so Items is already in its final shape
        // (bevel-m2.10.3). Re-plans in place, so it's safe on a re-attach too.
        _vm?.SetGrouping(_grouping);
        _vm?.SetSort(_sort, _windowlessLast);
        _vm?.Tray.Configure(_trayOverflowCap, _trayIconSize);
        _window?.SetAlwaysOnTop(_alwaysOnTop);
        ResizeGrip.IsVisible = !_locked;
        ShowDesktopButton.IsVisible = _showDesktop;
        ApplyTaskIconMetric();   // re-seed in case the tier was Configure()d after construction
        ApplyAppearance(_fontSize, _bgColor, _opacity);   // now attached — theme resources resolve

        // Compose the stacks region from the component list (bevel-aqr7 Task 14), now that _window —
        // and the real BarGeometry it owns — exists. Guarded like the Start menu just below: OnLoaded
        // re-runs on every re-attach, and the host must be built exactly once.
        if (_componentHost is null && _initialSettings is not null)
            InitComponentRegion(_initialSettings);

        // Hand the Start menu the reconciled Programs projection (bevel-d2z) so its cascade binds
        // the off-thread collection instead of enumerating + rendering icons on the UI thread.
        _startMenu ??= new StartMenu(_appEnv, _iconProvider, _quit, _restart, _vm?.StartMenu, _openSettings, _openFolder, _openSearch, _toggleDesktop, _desktopRunning);
        // The menu hosts its content in a Popup, which only opens once attached to a visual tree
        // (it needs a TopLevel). It contributes no layout size, so parenting it in the taskbar
        // grid is invisible but is what lets the Start menu appear on screen.
        if (_startMenu.Parent is null)
            RootGrid.Children.Add(_startMenu);

        // Subscribe exactly once. OnLoaded runs again on every re-attach to the visual tree; without
        // this guard a reparent would double-wire every handler (double tooltips, double reflows).
        // DataContext is assigned at construction (App.axaml.cs) so _vm is already live here.
        if (!_wired)
        {
            _wired = true;

            // Adaptive tray ink (bevel-m3, macOS template model): re-tint mirrored template glyphs to the
            // bar's contrast colour whenever a theme/variant switch changes Bevel.Brush.TrayText — dark on
            // bright bars (Silver/grey), light on dark (Luna blue). Colourful / self-contained icons are
            // left alone by TrayIconTint. A resource observable fires the initial value + every change.
            this.GetResourceObservable("Bevel.Brush.TrayText").Subscribe(
                new Avalonia.Reactive.AnonymousObserver<object?>(v =>
                {
                    if (v is Avalonia.Media.ISolidColorBrush b) _vm?.Tray.SetInk(b.Color);
                }));

            // Start-button badge: the Bevel mark (Tux on Linux), sized by the theme's StartBadgeSize metric.
            // A resource observable fires the initial value and every theme switch, so the badge rebuilds at
            // the new size (and detail level) live.
            this.GetResourceObservable("Bevel.Metric.StartBadgeSize").Subscribe(
                new Avalonia.Reactive.AnonymousObserver<object?>(v =>
                {
                    _startBadgeSize = v is double d and > 0 ? d : StartLogo.DefaultSize;
                    RebuildStartBadge();
                }));
            // The divider chases the Start button's REAL width (bevel-6x9z). ApplyStart alone is not
            // enough: it runs before layout, so the Bounds it would read are the previous caption's.
            // Badge-size and theme changes resize the button too, and this catches those for free.
            StartButton.GetObservable(Visual.BoundsProperty).Subscribe(
                new Avalonia.Reactive.AnonymousObserver<Rect>(_ => SyncStartDivider()));

            StartButton.Click += OnStartButtonClick;
            AddHandler(KeyDownEvent, OnTaskbarKeyDown, RoutingStrategies.Tunnel);

            // Menu-scoped key focus (bevel-vk4n): the Start menu's popup opening/closing drives the
            // become-key flip, so its arrow-key navigation and Escape actually reach the bar. The menu
            // also focuses its first item on open (StartMenu.OpenAsync).
            if (_startMenu is not null)
            {
                _startMenu.MenuPopupControl.Opened += (_, _) =>
                {
                    EnterMenuScope();
                    TraceMenuPlacement();
                };
                _startMenu.MenuPopupControl.Closed += (_, _) => ExitMenuScope();
            }

            WireFlyoutScope(TrayOverflowButton.Flyout);   // the "show hidden icons" tray overflow popover

            // Native global Ctrl+Esc / Option+Esc summon (bevel-vk4n). Installed only when a REAL native
            // window backs the taskbar — never in headless tests (where every TaskbarView would otherwise
            // register a process-global AppKit monitor). Gated to macOS via HasNativeWindow.
            if (_window?.HasNativeWindow == true)
                _startHotkeyMonitor = TaskbarNative.AddGlobalKeyDownMonitor(OnGlobalStartHotkey);
            AddHandler(PointerPressedEvent, OnWindowButtonMiddleClick, RoutingStrategies.Tunnel);

            // Re-flow button widths when the strip resizes, the row count changes, or the window
            // list changes. Every trigger routes through QueueLayout so a burst (e.g. K buttons
            // realized at once) collapses to a single Background-priority reflow instead of K+1.
            WindowButtonScroller.SizeChanged += (_, _) => QueueLayout();
            if (_vm is not null)
                _vm.Items.CollectionChanged += OnWindowsChanged;   // grouped/ungrouped display set
            // Re-run the width pass when a button's container is realized, so a newly-opened
            // window's button — created at Width 0 — animates up to the target AFTER it has rendered
            // at 0 (the XP grow-in), rather than being snapped to the target before it ever draws.
            WindowButtonArea.ContainerPrepared += OnButtonContainerPrepared;
            WindowButtonArea.ContainerClearing += OnButtonContainerClearing;
            // Bubble-phase handlers catch hovers even if a future template change leaves inner
            // content hit-testable; per-button direct handlers remain for redundancy.
            WindowButtonArea.AddHandler(InputElement.PointerEnteredEvent, OnTaskButtonPointerEntered, RoutingStrategies.Bubble);
            WindowButtonArea.AddHandler(InputElement.PointerExitedEvent, OnTaskButtonPointerExited, RoutingStrategies.Bubble);
            // Dismiss a hover preview once the cursor strays >30px from its anchor button — PointerExited
            // only fires at the button edge, so this closes the preview when the pointer drifts across the
            // strip's gaps without landing on another button (user request).
            WindowButtonArea.AddHandler(InputElement.PointerMovedEvent, OnTaskbarPointerMoved, RoutingStrategies.Bubble);

            // Close the transient hover surfaces — the window preview and the Start menu (with its
            // submenus) — when the whole app loses focus. The taskbar window is deliberately
            // non-activating (SetCanBecomeKeyWindow=false), so Window.Deactivated never fires reliably;
            // IActivatableLifetime raises Background on the app-level resign-active, which is the signal
            // we actually want and which does NOT fire when our own in-app popup opens (user request).
            if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime life)
                life.Deactivated += OnAppDeactivated;

            // Drag-to-resize the bar in whole button-row steps (bevel-0ml).
            if (_window is not null)
                _window.RowsChanged += _ => ApplyRowLayout();
            ResizeGrip.PointerPressed += OnGripPressed;
            ResizeGrip.PointerMoved += OnGripMoved;
            ResizeGrip.PointerReleased += OnGripReleased;

            // Overflow chevrons (bevel-m2.10.2): scroll the wrapped button rows a row at a time, and
            // keep their visible/enabled state in sync as the strip scrolls.
            ScrollUpBtn.Click += (_, _) => ScrollRows(-1);
            ScrollDownBtn.Click += (_, _) => ScrollRows(1);
            WindowButtonScroller.ScrollChanged += (_, _) => UpdateOverflowChevrons();
        }

        StartButton.MaxHeight = StartButtonMaxHeight();
        ApplyRowLayout();
        // A posted pass after the scroller has real bounds, so the initially-seeded buttons get a
        // width (they start at 0) even if their SizeChanged fired before we subscribed. Also wires
        // pointer handlers for containers realized before ContainerPrepared was subscribed.
        QueueLayout();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // Release the process-global hotkey monitor with the view (bevel-vk4n) so a torn-down taskbar
        // doesn't leave a dangling AppKit monitor pointing at freed managed state.
        if (_startHotkeyMonitor != IntPtr.Zero)
        {
            TaskbarNative.RemoveMonitor(_startHotkeyMonitor);
            _startHotkeyMonitor = IntPtr.Zero;
        }
        base.OnUnloaded(e);
    }

    /// <summary>
    /// Collapses the many layout triggers (strip resize, collection change, each container
    /// realization) into one Background-priority reflow. <see cref="LayoutButtons"/> is an O(n)
    /// pass that also allocates, so running it once per burst instead of once per trigger matters
    /// when a window-open realizes several buttons in the same dispatcher frame.
    /// </summary>
    private void QueueLayout()
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _layoutQueued = false;
            TaskbarLog.Debug($"LAYOUT reflow: {_vm?.Windows.Count ?? 0} buttons");
            LayoutButtons();
            WireAllTaskButtons();
            UpdateOverflowChevrons();
        }, DispatcherPriority.Background);
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TaskbarLog.Debug($"Windows.CollectionChanged action={e.Action} " +
            $"new=[{Titles(e.NewItems)}] old=[{Titles(e.OldItems)}] -> HideTooltip + QueueLayout");
        HideTaskbarTooltip();
        QueueLayout();
    }

    private static string Titles(System.Collections.IList? items)
    {
        if (items is null) return "";
        var names = new System.Collections.Generic.List<string>();
        foreach (var it in items)
            names.Add(it is TaskItemViewModel vm ? $"'{vm.Title}'" : it?.ToString() ?? "null");
        return string.Join(", ", names);
    }

    /// <summary>Win2000 hover delay (SPI_GETMOUSEHOVERTIME default).</summary>
    private static readonly TimeSpan TooltipShowDelay = TimeSpan.FromMilliseconds(150);
    /// <summary>Stale-then-refresh hover previews (bevel-c04q). Shared across buttons — capacity-bounded.</summary>
    private readonly WindowPreviewCache _previewCache = new();

    private void OnButtonContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is Control container)
            WireTaskButton(container);
        QueueLayout();
    }

    private void OnButtonContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is Control container && FindTaskButton(container) is { } button)
        {
            button.PointerEntered -= OnTaskButtonPointerEntered;
            button.PointerExited -= OnTaskButtonPointerExited;
            if (ReferenceEquals(_tooltipAnchor, button))
                HideTaskbarTooltip();
        }
    }

    private void WireAllTaskButtons()
    {
        foreach (var container in WindowButtonArea.GetRealizedContainers())
        {
            if (container is Control control)
                WireTaskButton(control);
        }
    }

    private void WireTaskButton(Control container)
    {
        if (FindTaskButton(container) is not { } button) return;
        // Apply the active button-height tier (bevel-m2.10.1); the template's 24 is the Normal default.
        button.Height = TaskbarTheme.ButtonHeight;
        button.PointerEntered -= OnTaskButtonPointerEntered;
        button.PointerExited -= OnTaskButtonPointerExited;
        button.PointerEntered += OnTaskButtonPointerEntered;
        button.PointerExited += OnTaskButtonPointerExited;
        // The pressed (sunken) state needs no wiring: TaskButton never self-toggles, so IsChecked stays
        // the OneWay projection of IsFocused the template binds (bevel-zk4a).
        // A grouped-app button carries a windows-list Flyout; bind its open/close to the key-focus scope
        // so keyboard users can arrow through the group's windows (bevel-vk4n). Idempotent per flyout.
        WireFlyoutScope(button.Flyout);
    }

    /// <summary>Clicking a mirrored tray icon forwards the click (with its button + modifiers) to the
    /// real menu-bar status item, so the owning app reveals its menu (spec §5.5, bevel-m3.3).</summary>
    private void OnTrayIconPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not TrayItemViewModel item || _vm is null) return;
        var props = e.GetCurrentPoint(c).Properties;
        var button = props.IsRightButtonPressed ? TrayButton.Right : TrayButton.Left;
        e.Handled = true;
        // Release the implicit pointer capture: the consolidated reveal (bevel-6fin) is a slow async
        // round-trip (self-addressed move + press in the helper), and if this handler held the press
        // gesture, every later click funnelled back to THIS image (observed: all clicks routing to the
        // first-pressed icon). Fire-and-forget so the press gesture ends immediately.
        e.Pointer.Capture(null);
        _ = _vm.Tray.Forward(item.Id, button, ToTrayModifiers(e.KeyModifiers));
    }

    /// <summary>Keyboard operability for a mirrored tray icon (bevel-vk4n): Enter/Space forwards a left
    /// activation (Shift/Ctrl/Alt/Cmd carried through), the same as a left-click on the icon. Tray icons
    /// are plain Images, so without this a keyboard/AT user could never operate the notification area.</summary>
    private async void OnTrayIconKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is not Control c || c.DataContext is not TrayItemViewModel item || _vm is null) return;
        e.Handled = true;
        await _vm.Tray.Forward(item.Id, TrayButton.Left, ToTrayModifiers(e.KeyModifiers));
    }

    private static TrayModifiers ToTrayModifiers(KeyModifiers mods)
    {
        var result = TrayModifiers.None;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= TrayModifiers.Shift;
        if (mods.HasFlag(KeyModifiers.Control)) result |= TrayModifiers.Control;
        if (mods.HasFlag(KeyModifiers.Alt)) result |= TrayModifiers.Option;
        if (mods.HasFlag(KeyModifiers.Meta)) result |= TrayModifiers.Command;
        return result;
    }

    private static ToggleButton? FindTaskButton(Control container) =>
        container as ToggleButton ?? container.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

    private void OnTaskButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        var anchor = (e.Source as Control)?.FindAncestorOfType<ToggleButton>()
                     ?? sender as ToggleButton;
        if (anchor?.DataContext is not TaskItemViewModel vm)
            return;

        _tooltipAnchor = anchor;
        _tooltipTimer?.Stop();
        _tooltipTimer = new DispatcherTimer { Interval = TooltipShowDelay };
        _tooltipTimer.Tick += (_, _) =>
        {
            _tooltipTimer?.Stop();
            if (ReferenceEquals(_tooltipAnchor, anchor))
                ShowTaskbarTooltip(anchor, vm);
        };
        _tooltipTimer.Start();
    }

    private void OnTaskButtonPointerExited(object? sender, PointerEventArgs e)
    {
        var anchor = (e.Source as Control)?.FindAncestorOfType<ToggleButton>()
                     ?? sender as ToggleButton;
        _tooltipTimer?.Stop();
        if (anchor is null || !ReferenceEquals(_tooltipAnchor, anchor)) return;
        _tooltipAnchor = null;
        HideTaskbarTooltip();
    }

    private async void ShowTaskbarTooltip(Control anchor, TaskItemViewModel vm)
    {
        if (anchor is not ToggleButton button) return;
        TaskbarTooltipText.Text = vm.StatusText;
        PreviewFrame.IsVisible = false;
        PreviewImage.Source = null;
        TooltipPopup.PlacementTarget = button;
        TooltipPopup.IsOpen = true;

        // Stale-then-update (bevel-c04q): paint a cached PNG immediately so hover feels instant;
        // refresh from CaptureWindow in the background and swap when a fresher frame arrives.
        var gen = ++_previewGeneration;
        var windowKey = vm.Id.Value;
        if (_previewCache.TryGet(windowKey, out var stale) && stale.Length > 0)
            TryApplyPreviewPng(stale, gen, anchor, button, vm);

        byte[]? png = null;
        if (_vm?.Model is { } model)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { png = await model.CaptureWindowAsync(vm.Id, 240, 160, cts.Token); }
            catch { png = null; }   // timeout / transport / capture failure → keep stale (if any)
        }
        if (gen != _previewGeneration) return;
        if (png is null || png.Length == 0) return;
        if (!ReferenceEquals(_tooltipAnchor, anchor) || !TooltipPopup.IsOpen
            || !ReferenceEquals(button.DataContext, vm)) return;
        _previewCache.Set(windowKey, png);
        TryApplyPreviewPng(png, gen, anchor, button, vm);
    }

    private void TryApplyPreviewPng(byte[] png, int gen, Control anchor, ToggleButton button, TaskItemViewModel vm)
    {
        if (gen != _previewGeneration) return;
        if (!ReferenceEquals(_tooltipAnchor, anchor) || !TooltipPopup.IsOpen
            || !ReferenceEquals(button.DataContext, vm)) return;
        try
        {
            using var ms = new System.IO.MemoryStream(png);
            (PreviewImage.Source as IDisposable)?.Dispose();
            PreviewImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            PreviewFrame.IsVisible = true;
        }
        catch { /* undecodable png → title only */ }
    }

    private void HideTaskbarTooltip()
    {
        if (TooltipPopup.IsOpen)
            TaskbarLog.Debug("TOOLTIP hide (open=true->false)");
        _tooltipTimer?.Stop();
        _tooltipAnchor = null;
        TooltipPopup.IsOpen = false;
        TooltipPopup.PlacementTarget = null;
        _previewGeneration++;   // discard any in-flight capture for the button we're leaving
        (PreviewImage.Source as IDisposable)?.Dispose();
        PreviewImage.Source = null;
        PreviewFrame.IsVisible = false;
    }

    /// <summary>How far (logical px) the cursor may drift from the preview's anchor button before the
    /// preview is dismissed.</summary>
    private const double PreviewDismissDistance = 30;

    /// <summary>Closes the hover preview (or cancels a pending one) once the cursor is more than
    /// <see cref="PreviewDismissDistance"/> px outside the anchor button's bounds.</summary>
    private void OnTaskbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tooltipAnchor is null) return;   // nothing shown or pending
        var p = e.GetPosition(_tooltipAnchor);
        // Distance from the pointer to the anchor's local rect (0,0 .. W,H); 0 while inside it.
        var dx = Math.Max(0, Math.Max(-p.X, p.X - _tooltipAnchor.Bounds.Width));
        var dy = Math.Max(0, Math.Max(-p.Y, p.Y - _tooltipAnchor.Bounds.Height));
        if (dx * dx + dy * dy > PreviewDismissDistance * PreviewDismissDistance)
            HideTaskbarTooltip();
    }

    /// <summary>The app moved to the background (user clicked another application). Dismiss the
    /// transient surfaces so they don't linger over whatever now has focus.</summary>
    private void OnAppDeactivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind != ActivationKind.Background) return;
        HideTaskbarTooltip();
        _startMenu?.Close();
        _openTaskMenu?.Hide();   // the app-centric task-button menu (bevel-ww71) — same non-activating reason
    }

    /// <summary>Start button height cap, in button rows (user: "cap start at 2x-3x row height").</summary>
    private const int StartMaxRows = 3;

    /// <summary>
    /// Pins the content height to the current row count and re-flows the buttons. The window's
    /// content presenter doesn't reliably stretch to a runtime height change, so without an
    /// explicit height the RootGrid sizes to its content and the full-height Start button and tray
    /// don't track the taller bar's real edges (bevel-0ml).
    /// </summary>
    /// <summary>Open the app-centric taskbar-button menu (bevel-ww71) — app header, per-window submenu,
    /// Quit/Force-Quit — built in code so the Alt swap + nesting stay simple.</summary>
    private Avalonia.Controls.MenuFlyout? _openTaskMenu;

    /// <summary>Serializes OnTaskButtonContextRequested: a second right-click landing inside the
    /// prefetch window would otherwise open a second flyout and orphan the first's global monitor.</summary>
    private bool _taskMenuOpenInFlight;

    private async void OnTaskButtonContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control c || !TaskButtonMenu.Recognizes(c.DataContext)) return;
        e.Handled = true;   // decided synchronously — must precede the first await

        if (_taskMenuOpenInFlight) return;
        _taskMenuOpenInFlight = true;
        try
        {
            // Tab prefetch (bevel-a40b): an open MenuFlyout never repaints, so tabs must exist BEFORE
            // TryShow. The await keeps the UI thread free; the budget caps how long the menu can lag
            // behind the right-click when the target app answers slowly (no tabs beats a stalled menu).
            var dc = c.DataContext;
            var tabs = await PrefetchTabsAsync(dc);

            // The projector mutates the strip in place during the await (remove/move/insert on the
            // very events a right-click races with — target app quitting, windows re-ordering). A
            // rebound container would show app B's menu with app A's tabs; a detached one would
            // ShowAt a control with no visual root. Both invalidate this click — drop it.
            if (!ReferenceEquals(c.DataContext, dc) || c.GetVisualRoot() is null)
            {
                DisposeTabRows(tabs);
                return;
            }

            // Dismiss any still-open prior menu BEFORE opening the new one, so its Closed handler
            // (which removes its global mouse monitor) runs first. The monitors are now per-token
            // (ce-review P0 fix in TaskbarWindow), but keeping the ordering clean avoids two
            // monitors briefly both firing on the same outside click.
            _openTaskMenu?.Hide();
            _openTaskMenu = null;

            if (TaskButtonMenu.TryShow(c, dc, tabs, _tabProvider,
                    onActivateForeign: () => _window?.CancelKeyFocusHandback()) is { } menu)
            {
                _openTaskMenu = menu;
                // The menu is already shown (TryShow → ShowAt); enter the key-focus scope now and exit on
                // close, so its arrow/Enter/Escape navigation reaches the bar (bevel-vk4n).
                EnterMenuScope();
                menu.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_openTaskMenu, menu)) _openTaskMenu = null;
                    ExitMenuScope();
                    // Skia-backed favicon bitmaps are unmanaged memory the GC can't see — release
                    // them with the menu (same pattern as the hover preview's bitmap swap).
                    DisposeTabRows(tabs);
                };
            }
            else
            {
                DisposeTabRows(tabs);
            }
        }
        catch (Exception ex)
        {
            // async void has no other backstop — an escape here is an unhandled dispatcher
            // exception, i.e. a dead taskbar over a context menu.
            TaskbarLog.Debug($"TASKMENU open failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _taskMenuOpenInFlight = false;
        }
    }

    private async Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>?> PrefetchTabsAsync(object? dc)
    {
        var bundleId = dc switch
        {
            TaskGroupViewModel g => g.BundleId,
            TaskItemViewModel t => t.BundleId,
            _ => null,
        };
        if (_tabProvider is null || !_tabProvider.SupportsApp(bundleId)) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TabPrefetchBudget);
            var tabs = await _tabProvider.GetTabsAsync(bundleId!, cts.Token);
            // Favicon PNGs decode into bitmaps OFF the UI thread (never-block rule) — Build then
            // only composes ready Image sources. DecodeToWidth(16) keeps a 192px cache blob from
            // decoding at full size for a 16px row. A blob that fails to decode is an icon-less row.
            var rows = await Task.Run(() => tabs.Select(t =>
            {
                Avalonia.Media.Imaging.Bitmap? icon = null;
                if (t.IconPng is { Length: > 0 } png)
                {
                    try { icon = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(new System.IO.MemoryStream(png), 16); }
                    catch { /* malformed cache blob */ }
                }
                return new TaskButtonMenu.TabMenuRow(t, icon);
            }).ToList());
            // Enumeration over budget is killed in the provider (empty list); enrichment over
            // budget is abandoned there (unenriched list). Neither throws — log every outcome so
            // "0 tabs in ~budget ms" is readable as a timeout. Elapsed includes icon decode.
            TaskbarLog.Debug($"TABS prefetch {bundleId}: {tabs.Count} in {sw.ElapsedMilliseconds}ms");
            return rows;
        }
        catch (Exception ex)
        {
            TaskbarLog.Debug($"TABS prefetch failed for {bundleId}: {ex.GetType().Name} after {sw.ElapsedMilliseconds}ms");
            return null;   // timeout / target app gone — the menu just opens without a Tabs section
        }
    }

    private static void DisposeTabRows(IReadOnlyList<TaskButtonMenu.TabMenuRow>? rows)
    {
        if (rows is null) return;
        foreach (var row in rows) row.Icon?.Dispose();
    }

    /// <summary>How long a right-click may wait for the target app's tab list before the menu opens
    /// without one. Sized to the slowest live measurement: a batched enumeration of Arc's ~100-tab
    /// sidebar takes ~0.65 s of Apple Events alone (plus osascript spawn), so 700 ms starved it and
    /// the menu permanently lost its Tabs section on big browsers. iTerm-sized apps answer in
    /// 100–300 ms regardless.</summary>
    private static readonly TimeSpan TabPrefetchBudget = TimeSpan.FromMilliseconds(1500);

    private void ApplyRowLayout()
    {
        RootGrid.Height = TaskbarTheme.HeightForRows(_window?.Rows ?? 1);
        // Track the row count here too (bevel-kclq): the Start button's cap feeds the Start menu's popup
        // anchor, so a cap left at a taller row count parks the menu above the bar.
        StartButton.MaxHeight = StartButtonMaxHeight();
        var rows = _window?.Rows ?? 1;
        // Log the WINDOW's own geometry, not just the row count. Every popup anchored inside the bar —
        // the Start menu, task-button tooltips, the tray flyouts — derives its position from this, so if
        // the window's Position or size disagrees with where it is drawn, they are ALL offset by the same
        // amount. The Start-menu gap reproduced on tooltips too, which is what rules out anything
        // specific to the Start button and points here (bevel-kclq follow-up).
        var screen = _window?.Screens?.Primary ?? _window?.Screens?.All?.FirstOrDefault();
        TaskbarLog.Debug(
            $"ApplyRowLayout rows={rows} rootHeight={RootGrid.Height} scaling={_window?.RenderScaling} " +
            $"winPos={_window?.Position} winSize={_window?.Bounds.Size} winH={_window?.Height} " +
            $"expectedH={TaskbarTheme.HeightForRows(rows)} screen={screen?.Bounds} workArea={screen?.WorkingArea} " +
            $"band={_window?.GetWorkAreaBand()}");
        _vm?.Tray.SetRows(rows);   // tray visible cap is PER ROW, and it lays out that many rows (bevel-m3)
        Clock.SetRows(rows);       // date drops under the time on a multi-row bar
        LayoutButtons();
    }

    // ── Drag-to-resize (bevel-0ml) ──────────────────────────────────────

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_locked) return;   // locked taskbar can't be resized (bevel-cust.behavior)
        HideTaskbarTooltip();
        _resizing = true;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || _window is null) return;
        var primary = _window.Screens?.Primary ?? _window.Screens?.All?.FirstOrDefault();
        if (primary is null) return;

        var scale = primary.Scaling <= 0 ? 1.0 : primary.Scaling;
        var screenY = ResizeGrip.PointToScreen(e.GetPosition(ResizeGrip)).Y;
        var screenBottom = primary.Bounds.Y + primary.Bounds.Height;
        var desiredHeight = (screenBottom - screenY) / scale;
        var rows = (int)Math.Round((desiredHeight - TaskbarTheme.TaskbarHeight) / TaskbarTheme.RowHeight) + 1;
        _window.SetRows(rows);
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Window-button sizing (U11) ──────────────────────────────────────

    // Icon-only tier (bevel-m2.10): the narrowest a shrinking button goes, and the width at/above
    // which it still shows its text label. Below the label-hide threshold the label is dropped and only
    // a centred icon remains (the hover tooltip still carries the full title).
    //
    // All three are derived from the tier's glyph edge (bevel-c54t) rather than hardcoded around 16px:
    // a 32px Big-tier icon needs a ≥40px button, and the pre-c54t constants (24/34/40) fall out exactly
    // at the 16px default, so Small/Normal behaviour is bit-identical.
    private const int BaseTaskIconSize = 16;

    /// <summary>Narrowest an icon-only button goes: the glyph plus its 8px horizontal padding.</summary>
    private static int IconOnlyFloorFor(int iconSize) => iconSize + 8;

    /// <summary>At/above this width a shrinking button still earns its text label.</summary>
    private static double LabelHideThresholdFor(int iconSize) => iconSize + 18;

    /// <summary>
    /// Sizes the live window buttons per the configured display mode (bevel-m2.10):
    /// <list type="bullet">
    /// <item><b>Fixed</b> — every button stays at the max width and the strip scrolls when it overflows.</item>
    /// <item><b>ShrinkToFit</b> (default) — buttons share the strip (available / perRow). They keep
    /// their label down to the text floor (<see cref="_minButtonWidth"/>); when even that won't fit
    /// they drop to icon-only and shrink to the icon-only floor.</item>
    /// </list>
    /// The computed width/label are pushed onto each live VM — the template's Width transition
    /// animates the change, driving both the steady-state resize as the strip fills AND the XP
    /// grow-in (from the VM's initial Width 0). Buttons animating out
    /// (<see cref="TaskItemViewModel.IsClosing"/>) are skipped so they finish shrinking to 0.
    /// </summary>
    private void LayoutButtons()
    {
        if (_vm is null) return;
        var live = _vm.Items.Where(w => !w.IsClosing).ToList();
        if (live.Count == 0) return;

        var available = WindowButtonScroller.Bounds.Width;
        if (available <= 0) return; // not laid out yet — SizeChanged will re-run this

        var (width, showLabel) = ComputeButtonLayout(
            _widthMode, available, live.Count, _window?.Rows ?? 1, _maxButtonWidth, _minButtonWidth, _buttonLabels,
            TaskbarTheme.TaskIconSize);

        foreach (var vm in live)
        {
            vm.Width = width;         // transitions animate the resize / grow-in
            vm.ShowLabel = showLabel; // icon-only tier when crowded
            vm.Opacity = 1;           // reveal (buttons are added at Opacity 0)
        }
    }

    /// <summary>
    /// Pure width/label policy for the window buttons (bevel-m2.10) — extracted so the mode logic is
    /// unit-testable without a visual tree. <b>Fixed</b> → every button at <paramref name="max"/>.
    /// <b>ShrinkToFit</b> → each button gets an equal share of the row (<paramref name="available"/> ÷
    /// buttons-per-row); labelled down to the text floor (<paramref name="minButtonWidth"/>), then
    /// icon-only down to the icon-only floor, dropping the label below
    /// the label-hide threshold.
    /// </summary>
    /// <summary>Icon-only buttons never grow past this — a square-ish hit target, not a wide empty button.
    /// Scales with the tier's glyph so a 32px Big icon gets a 56px slot, not a 40px one (bevel-c54t).</summary>
    private static double IconOnlyMaxFor(int iconSize) => iconSize + 24;

    internal static (double Width, bool ShowLabel) ComputeButtonLayout(
        TaskbarButtonWidthMode mode, double available, int count, int rows, double max, int minButtonWidth,
        TaskbarButtonLabels labels = TaskbarButtonLabels.Auto, int iconSize = BaseTaskIconSize)
    {
        var iconOnlyFloor = IconOnlyFloorFor(iconSize);
        var iconOnlyMax = IconOnlyMaxFor(iconSize);

        // IconOnly (macOS-Dock / KDE icons-only): always icon-sized, never labelled, in any width mode.
        if (labels == TaskbarButtonLabels.IconOnly)
        {
            if (mode == TaskbarButtonWidthMode.Fixed || count <= 0)
                return (iconOnlyMax, false);
            var perRowIo = (int)Math.Ceiling(count / (double)Math.Max(1, rows));
            var idealIo = (available / Math.Max(1, perRowIo)) - 2;
            return (Math.Clamp(idealIo, iconOnlyFloor, iconOnlyMax), false);
        }

        if (mode == TaskbarButtonWidthMode.Fixed || count <= 0)
            return (max, true);

        var perRow = (int)Math.Ceiling(count / (double)Math.Max(1, rows));
        const double perButtonMargin = 2;   // Margin(1,·) => 2px horizontal
        var ideal = (available / Math.Max(1, perRow)) - perButtonMargin;
        var floor = Math.Clamp((double)minButtonWidth, iconOnlyFloor, max);

        // Always-labels: keep the label and never fall below the text floor. If that overflows the row,
        // the wrap/scroll chevrons handle it — we don't drop to icon-only.
        if (labels == TaskbarButtonLabels.Always)
            return (Math.Clamp(ideal, floor, max), true);

        if (ideal >= floor)
            return (Math.Min(ideal, max), true);   // roomy: labelled, up to the max

        // Crowded past the text floor: shrink further, dropping the label once too narrow.
        var width = Math.Clamp(ideal, iconOnlyFloor, floor);
        return (width, width >= LabelHideThresholdFor(iconSize));
    }

    /// <summary>
    /// Shows the up/down overflow chevrons only when the wrapped button rows are taller than the bar,
    /// and disables whichever arrow can't move further (bevel-m2.10.2). When everything fits again the
    /// scroll offset is reset so buttons never stay parked out of view.
    /// </summary>
    private void UpdateOverflowChevrons()
    {
        var sv = WindowButtonScroller;
        var overflow = sv.Extent.Height - sv.Viewport.Height > 0.5;
        OverflowChevrons.IsVisible = overflow;

        if (!overflow)
        {
            if (sv.Offset.Y > 0.5)
                sv.Offset = sv.Offset.WithY(0);
            return;
        }

        var maxY = sv.Extent.Height - sv.Viewport.Height;
        ScrollUpBtn.IsEnabled = sv.Offset.Y > 0.5;
        ScrollDownBtn.IsEnabled = sv.Offset.Y < maxY - 0.5;
    }

    /// <summary>Scrolls the button strip by one button-row in <paramref name="direction"/> (-1 up, +1 down).</summary>
    private void ScrollRows(int direction)
    {
        var sv = WindowButtonScroller;
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var y = Math.Clamp(sv.Offset.Y + direction * TaskbarTheme.RowHeight, 0, maxY);
        sv.Offset = sv.Offset.WithY(y);   // ScrollChanged re-runs UpdateOverflowChevrons for enabled state
    }

    // ── Start menu ──────────────────────────────────────────────────────

    private void OnStartButtonClick(object? sender, RoutedEventArgs e) => ToggleStartMenu();

    /// <summary>Opens the Start menu if closed, closes it if open. Shared by the Start button click, the
    /// in-window Ctrl+Esc handler, and the native global hotkey (bevel-vk4n).</summary>
    /// <param name="fromKeyboard">True for Ctrl+Esc / the global hotkey. Threads through to
    /// <see cref="StartMenu.OpenAsync"/>, which only paints the opening selection for a keyboard
    /// summons — a mouse open highlights nothing until the pointer picks a row.</param>
    private void ToggleStartMenu(bool fromKeyboard = false)
    {
        if (_startMenu is null) return;
        if (_startMenu.IsOpen) _startMenu.Close();
        else _ = _startMenu.OpenAsync(StartButton, fromKeyboard);
    }

    private void OnTaskbarKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Esc (Win2000 standard) or Option+Esc (macOS-friendly) opens the menu. This handler is now
        // genuinely reachable: a menu open makes the taskbar key (SetKeyFocusAllowed), and the native
        // global monitor summons the menu from idle when the bar isn't key yet (bevel-vk4n).
        if (e.Key == Key.Escape && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            if (_startMenu is not null && !_startMenu.IsOpen)
            {
                _ = _startMenu.OpenAsync(StartButton, fromKeyboard: true);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _startMenu is { IsOpen: true })
        {
            // While a type-to-search query is live, the first Escape clears the filter and the NEXT one
            // closes the menu (bevel-cezo) — the same two-step Escape a search field anywhere else gives.
            if (!_startMenu.ClearSearchIfActive())
                _startMenu.Close();
            e.Handled = true;
        }
    }

    // ── Menu-scoped key focus (bevel-vk4n) ───────────────────────────────

    /// <summary>Marks a menu/popover the taskbar owns as open: on the first one, flip the window to allow
    /// key focus so its keystrokes (arrows / Enter / Escape) land on the bar.</summary>
    private void EnterMenuScope()
    {
        if (_menuScopeDepth++ == 0)
            _window?.SetKeyFocusAllowed(true);
    }

    /// <summary>Marks one owned menu/popover as closed: on the last one, flip the window back to non-key so
    /// focus returns to the app that had it.</summary>
    private void ExitMenuScope()
    {
        if (_menuScopeDepth > 0 && --_menuScopeDepth == 0)
            _window?.SetKeyFocusAllowed(false);
    }

    /// <summary>Idempotently binds a flyout's open/close to the key-focus scope, so a task/tray/stack/group
    /// popover drives the become-key flip just like the Start menu does.</summary>
    private void WireFlyoutScope(Avalonia.Controls.Primitives.FlyoutBase? flyout)
    {
        if (flyout is null || !_scopedFlyouts.Add(flyout)) return;
        flyout.Opened += (_, _) => EnterMenuScope();
        flyout.Closed += (_, _) => ExitMenuScope();
    }

    /// <summary>Handles the native global Ctrl+Esc / Option+Esc summon (bevel-vk4n). Called off the AppKit
    /// monitor thread, so it marshals the toggle back to the UI thread. Escape's macOS virtual key is 53;
    /// Control = 1&lt;&lt;18, Option = 1&lt;&lt;19 in NSEvent.modifierFlags.</summary>
    private void OnGlobalStartHotkey(ulong keyCode, ulong modifierFlags)
    {
        const ulong escKeyCode = 53;
        const ulong controlFlag = 1UL << 18;
        const ulong optionFlag = 1UL << 19;
        if (keyCode != escKeyCode) return;
        if ((modifierFlags & (controlFlag | optionFlag)) == 0) return;
        Dispatcher.UIThread.Post(() => ToggleStartMenu(fromKeyboard: true));
    }
}
