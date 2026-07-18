using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private Action? _quit;
    private Action? _restart;
    private Action? _openSettings;
    private int _maxButtonWidth = 160;
    private int _minButtonWidth = 80;
    private TaskbarButtonWidthMode _widthMode = TaskbarButtonWidthMode.ShrinkToFit;
    private bool _groupWindows;
    private TaskbarWindow? _window;
    private TaskbarViewModel? _vm;
    private bool _resizing;
    private DispatcherTimer? _tooltipTimer;
    private Control? _tooltipAnchor;
    private bool _wired;
    private bool _layoutQueued;

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
        IAppEnvironment? appEnv,
        IIconProvider? iconProvider = null,
        int buttonWidth = 160,
        Action? quit = null,
        Action? restart = null,
        TaskbarButtonWidthMode widthMode = TaskbarButtonWidthMode.ShrinkToFit,
        int minButtonWidth = 80,
        bool groupWindows = false,
        Action? openSettings = null,
        bool showClock = true,
        bool clock24Hour = true,
        bool clockShowSeconds = false,
        bool clockShowDate = false)
    {
        _appEnv = appEnv;
        _iconProvider = iconProvider;
        _maxButtonWidth = buttonWidth;
        _widthMode = widthMode;
        // Keep the text floor sane: never above the max, never below the icon-only floor.
        _minButtonWidth = Math.Clamp(minButtonWidth, IconOnlyFloor, buttonWidth);
        _groupWindows = groupWindows;
        _quit = quit;
        _restart = restart;
        _openSettings = openSettings;
        Clock.Configure(showClock, clock24Hour, clockShowSeconds, clockShowDate);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _vm = DataContext as TaskbarViewModel;
        _window = TopLevel.GetTopLevel(this) as TaskbarWindow;

        // Apply the grouping mode before the first layout so Items is already in its final shape
        // (bevel-m2.10.3). Re-plans in place, so it's safe on a re-attach too.
        _vm?.SetGrouping(_groupWindows);

        // Hand the Start menu the reconciled Programs projection (bevel-d2z) so its cascade binds
        // the off-thread collection instead of enumerating + rendering icons on the UI thread.
        _startMenu ??= new StartMenu(_appEnv, _iconProvider, _quit, _restart, _vm?.StartMenu, _openSettings);
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
            // Host-OS badge on the Start button: Windows flag / Apple / Tux, self-drawn vectors.
            StartLogoHost.Content = StartLogo.For(16);
            StartButton.Click += OnStartButtonClick;
            AddHandler(KeyDownEvent, OnTaskbarKeyDown, RoutingStrategies.Tunnel);

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

    /// <summary>Refreshes a folder stack's recent-contents list (and clears its new-item cue) as its
    /// button is clicked, so the flyout that opens right after shows the current folder (bevel-12g).</summary>
    private void OnStackButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is StackViewModel stack)
            stack.Refresh();
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

    private void ShowTaskbarTooltip(Control anchor, TaskItemViewModel vm)
    {
        if (anchor is not ToggleButton button) return;
        TaskbarLog.Debug($"TOOLTIP show for '{vm.Title}' (open={TooltipPopup.IsOpen}->true)");
        TaskbarTooltipText.Text = vm.StatusText;
        TooltipPopup.PlacementTarget = button;
        TooltipPopup.IsOpen = true;
    }

    private void HideTaskbarTooltip()
    {
        if (TooltipPopup.IsOpen)
            TaskbarLog.Debug("TOOLTIP hide (open=true->false)");
        _tooltipTimer?.Stop();
        _tooltipAnchor = null;
        TooltipPopup.IsOpen = false;
        TooltipPopup.PlacementTarget = null;
    }

    /// <summary>Start button height cap, in button rows (user: "cap start at 2x-3x row height").</summary>
    private const int StartMaxRows = 3;

    /// <summary>
    /// Pins the content height to the current row count and re-flows the buttons. The window's
    /// content presenter doesn't reliably stretch to a runtime height change, so without an
    /// explicit height the RootGrid sizes to its content and the full-height Start button and tray
    /// don't track the taller bar's real edges (bevel-0ml).
    /// </summary>
    private void ApplyRowLayout()
    {
        RootGrid.Height = TaskbarTheme.HeightForRows(_window?.Rows ?? 1);
        LayoutButtons();
    }

    // ── Drag-to-resize (bevel-0ml) ──────────────────────────────────────

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
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
            _widthMode, available, live.Count, _window?.Rows ?? 1, _maxButtonWidth, _minButtonWidth);

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
    internal static (double Width, bool ShowLabel) ComputeButtonLayout(
        TaskbarButtonWidthMode mode, double available, int count, int rows, double max, int minButtonWidth)
    {
        if (mode == TaskbarButtonWidthMode.Fixed || count <= 0)
            return (max, true);

        var perRow = (int)Math.Ceiling(count / (double)Math.Max(1, rows));
        const double perButtonMargin = 2;   // Margin(1,·) => 2px horizontal
        var ideal = (available / Math.Max(1, perRow)) - perButtonMargin;
        var floor = Math.Clamp((double)minButtonWidth, IconOnlyFloor, max);

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

    private async void OnStartButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_startMenu is null) return;
        if (_startMenu.IsOpen) _startMenu.Close();
        else await _startMenu.OpenAsync(StartButton);
    }

    private async void OnTaskbarKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Esc (Win2000 standard) or Option+Esc (macOS-friendly) opens the menu.
        if (e.Key == Key.Escape && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            if (_startMenu is not null && !_startMenu.IsOpen)
            {
                await _startMenu.OpenAsync(StartButton);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _startMenu is { IsOpen: true })
        {
            _startMenu.Close();
            e.Handled = true;
        }
    }
}
