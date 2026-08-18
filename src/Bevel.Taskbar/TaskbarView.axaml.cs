using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

public partial class TaskbarView : UserControl
{
    private StartMenu? _startMenu;
    private IAppEnvironment? _appEnv;
    private IIconProvider? _iconProvider;
    private ITabProvider? _tabProvider;
    private Action? _quit;
    private Action? _restart;
    private Action? _openSettings;
    private Action<Bevel.Core.Vfs.VfsPath>? _openFolder;
    private Action? _openSearch;
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
    private StackFileViewModel? _stackDragItem;
    private Point _stackDragStart;
    private System.Threading.Tasks.Task<IStorageFile?>? _stackDragFileTask;
    private int _fontSize;
    private string _bgColor = "";
    private int _opacity = 100;
    private TaskbarWindow? _window;
    private TaskbarViewModel? _vm;
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

    public TaskbarView() => InitializeComponent();

    public Button StartButtonControl => StartButton;
    public ItemsControl WindowButtonAreaControl => WindowButtonArea;
    public ClockWidget ClockControl => Clock;

    /// <summary>
    /// Supplies the Start menu's app environment + icon provider and the max button width. The
    /// window-button list itself now comes from the bound <see cref="TaskbarViewModel"/>
    /// (DataContext) via the background <see cref="ShellModel"/> — the taskbar no longer
    /// subscribes to window events or builds/mutates buttons by hand (bevel-d2z).
    /// </summary>
    public void Initialize(
        Bevel.Core.BevelSettings settings,
        IAppEnvironment? appEnv = null,
        IIconProvider? iconProvider = null,
        Action? quit = null,
        Action? restart = null,
        Action? openSettings = null,
        Action? toggleLock = null,
        Action<Bevel.Core.Vfs.VfsPath>? openFolder = null,
        Action? openSearch = null,
        ITabProvider? tabProvider = null)
    {
        // Non-settings wiring (PAL services + the shell-command callbacks).
        _appEnv = appEnv;
        _iconProvider = iconProvider;
        _tabProvider = tabProvider;
        _quit = quit;
        _restart = restart;
        _openSettings = openSettings;
        _openFolder = openFolder;
        _openSearch = openSearch;
        _toggleLock = toggleLock;

        // Every persisted setting flows from the one BevelSettings (bevel-ccs) — no more 25-param call.
        // These mirror ApplyLiveSettings; startup can't diverge from a live change because both read the
        // same fields. (Appearance is deferred to OnLoaded, since it reads theme resources that don't
        // resolve until the view is attached — so it's stored here, not applied.)
        _maxButtonWidth = settings.TaskbarButtonWidth;
        _widthMode = settings.TaskbarButtonWidthMode;
        _buttonSize = settings.TaskbarButtonSize;
        _minButtonWidth = Math.Clamp(settings.TaskbarMinButtonWidth, IconOnlyFloor, settings.TaskbarButtonWidth);
        _grouping = settings.TaskbarGrouping;
        _buttonLabels = settings.TaskbarButtonLabels;
        _middleClickCloses = settings.TaskbarMiddleClickCloses;
        _sort = settings.TaskbarWindowSort;
        _windowlessLast = settings.WindowlessAppsLast;
        _trayOverflowCap = settings.TaskbarTrayOverflowCap;
        _trayIconSize = settings.TaskbarTrayIconSize;
        _locked = settings.TaskbarLocked;
        _alwaysOnTop = settings.TaskbarAlwaysOnTop;
        _showDesktop = settings.TaskbarShowDesktopButton;
        Clock.Configure(settings.TaskbarShowClock, settings.TaskbarClock24Hour, settings.TaskbarClockShowSeconds, settings.TaskbarClockShowDate);
        ApplyStart(settings.TaskbarShowStart, settings.TaskbarStartLabel);
        _fontSize = settings.TaskbarFontSize;
        _bgColor = settings.TaskbarBackgroundColor;
        _opacity = settings.TaskbarOpacity;
    }

    /// <summary>Sets the Start button's visibility and caption (empty caption = logo only).</summary>
    private void ApplyStart(bool show, string label)
    {
        StartButton.IsVisible = show;
        StartLabelText.Text = label;
        StartLabelText.IsVisible = !string.IsNullOrEmpty(label);
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
        Clock.Configure(s.TaskbarShowClock, s.TaskbarClock24Hour, s.TaskbarClockShowSeconds, s.TaskbarClockShowDate);
        ApplyStart(s.TaskbarShowStart, s.TaskbarStartLabel);
        _grouping = s.TaskbarGrouping;
        _vm?.SetGrouping(_grouping);
        _sort = s.TaskbarWindowSort;
        _windowlessLast = s.WindowlessAppsLast;
        _vm?.SetSort(_sort, _windowlessLast);
        _buttonLabels = s.TaskbarButtonLabels;
        _middleClickCloses = s.TaskbarMiddleClickCloses;
        _fontSize = s.TaskbarFontSize;
        _bgColor = s.TaskbarBackgroundColor;
        _opacity = s.TaskbarOpacity;
        ApplyAppearance(_fontSize, _bgColor, _opacity);
        _vm?.Tray.Configure(s.TaskbarTrayOverflowCap, s.TaskbarTrayIconSize);
        _vm?.Tray.SetConsolidated(s.TaskbarConsolidateMenuBar);   // Strategy C (bevel-7hf4): in-process apply path

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
        _minButtonWidth = Math.Clamp(s.TaskbarMinButtonWidth, IconOnlyFloor, s.TaskbarButtonWidth);
        if (_buttonSize != s.TaskbarButtonSize)
        {
            _buttonSize = s.TaskbarButtonSize;
            TaskbarTheme.Configure(_buttonSize);
            _window?.ReapplyMetrics();     // resize + re-anchor + refresh band; raises RowsChanged → ApplyRowLayout
            StartButton.MaxHeight = TaskbarTheme.HeightForRows(StartMaxRows);
            ReapplyButtonHeights();
        }
        // Row count from the settings slider — the same knob as dragging the resize grip. SetRows is a
        // no-op when unchanged and clamps to the screen-derived MaxRows.
        if (_window != null && _window.Rows != s.TaskbarRows)
            _window.SetRows(s.TaskbarRows);
        LayoutButtons();
    }

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
            vm.ActivateCommand.Execute(null);
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

    // ── Stack flyout: mini-explorer rows (bevel-cust) — click to open, drag out to any app ──────────

    /// <summary>Single click/tap on a stack-flyout row opens the file (OpenCommand raises Opened, which
    /// dismisses the flyout). A drag gesture suppresses Tapped, so dragging never also opens the file.</summary>
    private void OnStackFileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StackFileViewModel vm)
            vm.OpenCommand.Execute(null);
    }

    /// <summary>Keyboard operability for a stack-flyout row (bevel-vk4n): Enter/Space opens the file, the
    /// same action as a click/tap.</summary>
    private void OnStackFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if ((sender as Control)?.DataContext is StackFileViewModel vm)
        {
            vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>On press we record the row and START resolving its IStorageFile, so that by the time the
    /// pointer moves the file is ready and <see cref="DragDrop.DoDragDrop"/> can run synchronously inside
    /// the move handler — awaiting the resolve first would drop the OS drag gesture.</summary>
    private void OnStackFilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed
            && (sender as Control)?.DataContext is StackFileViewModel vm)
        {
            _stackDragItem = vm;
            _stackDragStart = e.GetPosition(null);
            _stackDragFileTask = TopLevel.GetTopLevel(this)?.StorageProvider?.TryGetFileFromPathAsync(ToFileUri(vm.FullPath));
        }
    }

    /// <summary>Once the pointer moves past a small threshold with the button held, start an OS file-drag
    /// carrying the file (bevel-cust) so it can be dropped on Finder or any app. Fires only when the
    /// pre-resolve has completed, keeping DoDragDrop synchronous; otherwise a later move picks it up.</summary>
    private void OnStackFileMoved(object? sender, PointerEventArgs e)
    {
        if (_stackDragItem is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { _stackDragItem = null; return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _stackDragStart.X) < 4 && Math.Abs(pos.Y - _stackDragStart.Y) < 4) return;
        if (_stackDragFileTask is not { IsCompletedSuccessfully: true } resolved) return; // wait a frame for the file
        _stackDragItem = null;

        var file = resolved.Result;
        if (file is null) return;
        var data = new DataObject();
        data.Set(DataFormats.Files, new[] { file });
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    private static Uri ToFileUri(string path) => new UriBuilder { Scheme = "file", Host = string.Empty, Path = path }.Uri;

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
        ApplyAppearance(_fontSize, _bgColor, _opacity);   // now attached — theme resources resolve

        // Hand the Start menu the reconciled Programs projection (bevel-d2z) so its cascade binds
        // the off-thread collection instead of enumerating + rendering icons on the UI thread.
        _startMenu ??= new StartMenu(_appEnv, _iconProvider, _quit, _restart, _vm?.StartMenu, _openSettings, _openFolder, _openSearch);
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

            // Host-OS badge on the Start button: Windows flag / Apple / Tux, self-drawn vectors.
            StartLogoHost.Content = StartLogo.For(16);
            StartButton.Click += OnStartButtonClick;
            AddHandler(KeyDownEvent, OnTaskbarKeyDown, RoutingStrategies.Tunnel);

            // Menu-scoped key focus (bevel-vk4n): the Start menu's popup opening/closing drives the
            // become-key flip, so its arrow-key navigation and Escape actually reach the bar. The menu
            // also focuses its first item on open (StartMenu.OpenAsync).
            if (_startMenu is not null)
            {
                _startMenu.MenuPopupControl.Opened += (_, _) => EnterMenuScope();
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

        StartButton.MaxHeight = TaskbarTheme.HeightForRows(StartMaxRows);
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
    private static readonly TimeSpan TooltipShowDelay = TimeSpan.FromMilliseconds(400);

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
            button.Click -= OnTaskButtonClick;
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
        button.Click -= OnTaskButtonClick;
        button.Click += OnTaskButtonClick;
        // A grouped-app button carries a windows-list Flyout; bind its open/close to the key-focus scope
        // so keyboard users can arrow through the group's windows (bevel-vk4n). Idempotent per flyout.
        WireFlyoutScope(button.Flyout);
    }

    /// <summary>
    /// The pressed (sunken) state is a pure reflection of the shell's exclusive focus projection —
    /// <see cref="TaskItemViewModel.IsFocused"/>, bound OneWay to <c>IsChecked</c>. But a
    /// <see cref="ToggleButton"/> flips <c>IsChecked</c> locally on click, and the OneWay binding only
    /// re-asserts when <c>IsFocused</c> actually changes; when it doesn't (the clicked window doesn't
    /// take focus, or it's already unfocused when another window later grabs focus), the local flip
    /// sticks and the button stays pressed — leaving several buttons pressed at once. Snap it back to
    /// <c>IsFocused</c> here so the click never diverges; the genuine pressed state then follows the
    /// binding as focus events arrive.
    /// </summary>
    /// <summary>Clicking a mirrored tray icon forwards the click (with its button + modifiers) to the
    /// real menu-bar status item, so the owning app reveals its menu (spec §5.5, bevel-m3.3).</summary>
    private async void OnTrayIconPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not TrayItemViewModel item || _vm is null) return;
        var props = e.GetCurrentPoint(c).Properties;
        var button = props.IsRightButtonPressed ? TrayButton.Right : TrayButton.Left;
        e.Handled = true;
        await _vm.Tray.Forward(item.Id, button, ToTrayModifiers(e.KeyModifiers));
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

    /// <summary>Refreshes a folder stack's recent-contents list (and clears its new-item cue) as its
    /// button is clicked, so the flyout that opens right after shows the current folder (bevel-12g).</summary>
    private void OnStackButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is StackViewModel stack)
            stack.Refresh();
        // Bind this stack's recent-contents flyout to the key-focus scope (idempotent), so its rows are
        // keyboard-navigable while open (bevel-vk4n).
        if (sender is Button b)
            WireFlyoutScope(b.Flyout);
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

    private void OnTaskButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        // Snap the local toggle back to the real focus projection — for a single window and for a
        // group (whose pressed state means "some window of this app is focused"; its click opens the
        // flyout, it doesn't toggle focus). Prevents the "stuck pressed" divergence.
        button.IsChecked = button.DataContext switch
        {
            TaskItemViewModel vm => vm.IsFocused,
            TaskGroupViewModel g => g.IsFocused,
            _ => button.IsChecked,
        };
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

        // Live thumbnail (bevel-gcd). Tag the request so a superseded/late capture can't paint a stale or
        // wrong-window image (the same ToggleButton container gets rebound to another window by the
        // virtualizing strip); bound it with a timeout so a wedged helper can't leak an in-flight task per
        // hover (review: reliability/adversarial). 0/0 = let the helper apply its default size.
        var gen = ++_previewGeneration;
        byte[]? png = null;
        if (_vm?.Model is { } model)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { png = await model.CaptureWindowAsync(vm.Id, 0, 0, cts.Token); }
            catch { png = null; }   // timeout / transport / capture failure → no preview, never crash
        }
        // Bail unless this is still the current request AND the same button still shows the same window.
        if (gen != _previewGeneration || png is null || png.Length == 0) return;
        if (!ReferenceEquals(_tooltipAnchor, anchor) || !TooltipPopup.IsOpen
            || !ReferenceEquals(button.DataContext, vm)) return;
        try
        {
            using var ms = new System.IO.MemoryStream(png);
            (PreviewImage.Source as IDisposable)?.Dispose();   // release the previous bitmap (no per-hover leak)
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

            if (TaskButtonMenu.TryShow(c, dc, tabs, _tabProvider) is { } menu)
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
        var rows = _window?.Rows ?? 1;
        TaskbarLog.Debug($"ApplyRowLayout rows={rows} rootHeight={RootGrid.Height} scaling={_window?.RenderScaling}");
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
    // which it still shows its text label. Below LabelHideThreshold the label is dropped and only a
    // centred icon remains (the hover tooltip still carries the full title).
    private const int IconOnlyFloor = 24;
    private const double LabelHideThreshold = 34;

    /// <summary>
    /// Sizes the live window buttons per the configured display mode (bevel-m2.10):
    /// <list type="bullet">
    /// <item><b>Fixed</b> — every button stays at the max width and the strip scrolls when it overflows.</item>
    /// <item><b>ShrinkToFit</b> (default) — buttons share the strip (available / perRow). They keep
    /// their label down to the text floor (<see cref="_minButtonWidth"/>); when even that won't fit
    /// they drop to icon-only and shrink to <see cref="IconOnlyFloor"/>.</item>
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
            _widthMode, available, live.Count, _window?.Rows ?? 1, _maxButtonWidth, _minButtonWidth, _buttonLabels);

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
    /// icon-only down to <see cref="IconOnlyFloor"/>, dropping the label below
    /// <see cref="LabelHideThreshold"/>.
    /// </summary>
    /// <summary>Icon-only buttons never grow past this — a square-ish hit target, not a wide empty button.</summary>
    private const double IconOnlyMax = 40;

    internal static (double Width, bool ShowLabel) ComputeButtonLayout(
        TaskbarButtonWidthMode mode, double available, int count, int rows, double max, int minButtonWidth,
        TaskbarButtonLabels labels = TaskbarButtonLabels.Auto)
    {
        // IconOnly (macOS-Dock / KDE icons-only): always icon-sized, never labelled, in any width mode.
        if (labels == TaskbarButtonLabels.IconOnly)
        {
            if (mode == TaskbarButtonWidthMode.Fixed || count <= 0)
                return (IconOnlyMax, false);
            var perRowIo = (int)Math.Ceiling(count / (double)Math.Max(1, rows));
            var idealIo = (available / Math.Max(1, perRowIo)) - 2;
            return (Math.Clamp(idealIo, IconOnlyFloor, IconOnlyMax), false);
        }

        if (mode == TaskbarButtonWidthMode.Fixed || count <= 0)
            return (max, true);

        var perRow = (int)Math.Ceiling(count / (double)Math.Max(1, rows));
        const double perButtonMargin = 2;   // Margin(1,·) => 2px horizontal
        var ideal = (available / Math.Max(1, perRow)) - perButtonMargin;
        var floor = Math.Clamp((double)minButtonWidth, IconOnlyFloor, max);

        // Always-labels: keep the label and never fall below the text floor. If that overflows the row,
        // the wrap/scroll chevrons handle it — we don't drop to icon-only.
        if (labels == TaskbarButtonLabels.Always)
            return (Math.Clamp(ideal, floor, max), true);

        if (ideal >= floor)
            return (Math.Min(ideal, max), true);   // roomy: labelled, up to the max

        // Crowded past the text floor: shrink further, dropping the label once too narrow.
        var width = Math.Clamp(ideal, IconOnlyFloor, floor);
        return (width, width >= LabelHideThreshold);
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
    private void ToggleStartMenu()
    {
        if (_startMenu is null) return;
        if (_startMenu.IsOpen) _startMenu.Close();
        else _ = _startMenu.OpenAsync(StartButton);
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
                _ = _startMenu.OpenAsync(StartButton);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _startMenu is { IsOpen: true })
        {
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
        Dispatcher.UIThread.Post(ToggleStartMenu);
    }
}
