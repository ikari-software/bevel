using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// M2 onboarding and settings window. Shows Accessibility permission status,
/// work-area strategy picker, and run-at-login toggle.
/// Persists settings via <see cref="SettingsService"/>.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly SettingsService _settings;
    private readonly IPermissionBroker? _permissionBroker;
    private readonly IShellSession? _shellSession;
    private DispatcherTimer? _permPollTimer;

    // Guards the run-at-login checkbox against feedback: when we set IsChecked programmatically
    // (initial load, OS-status reconcile) we must NOT let OnRunAtLoginChanged re-register the OS.
    private bool _suppressRunAtLogin;

    /// <summary>Optional hook, set by the host, to push a settings change onto the live taskbar in the
    /// same process (e.g. reformat the running clock) so options apply instantly, not just on restart.</summary>
    public Action<BevelSettings>? ApplyLive { get; set; }

    // Parameterless constructor for the Avalonia runtime XAML loader / previewer (resolves AVLN3001,
    // which otherwise fires on every publish and is exactly the reachability class that breaks under
    // AOT — bevel-gww.7). It ONLY inflates the XAML — it must not run the DI ctor's LoadSettings /
    // permission-poll logic, which would dereference the unset service. The app always builds this
    // window through the DI constructor below.
    public OnboardingWindow()
    {
        _settings = null!;
        InitializeComponent();
    }

    public OnboardingWindow(SettingsService settings, IPermissionBroker? permissionBroker = null, IShellSession? shellSession = null)
    {
        InitializeComponent();
        _settings = settings;
        _permissionBroker = permissionBroker;
        _shellSession = shellSession;

        LoadSettings();
        StartPermissionPoll();
        _ = ReconcileRunAtLoginAsync();

        WASNudge.IsCheckedChanged += OnWorkAreaChanged;
        WASDockShim.IsCheckedChanged += OnWorkAreaChanged;
        WASNone.IsCheckedChanged += OnWorkAreaChanged;
        RunAtLoginCheck.IsCheckedChanged += OnRunAtLoginChanged;
        FixedWidthCheck.IsCheckedChanged += OnFixedWidthChanged;
        MinWidthSlider.ValueChanged += OnMinWidthChanged;
        GroupingCombo.SelectionChanged += OnGroupingChanged;
        LabelModeCombo.SelectionChanged += OnLabelModeChanged;
        MiddleClickCloseCheck.IsCheckedChanged += OnMiddleClickChanged;
        ShowStartCheck.IsCheckedChanged += (_, _) => PersistStart();
        StartLabelBox.TextChanged += (_, _) => PersistStart();
        ButtonSizeCombo.SelectionChanged += OnButtonSizeChanged;
        ShowClockCheck.IsCheckedChanged += OnClockChanged;
        Clock24Check.IsCheckedChanged += OnClockChanged;
        ClockSecondsCheck.IsCheckedChanged += OnClockChanged;
        ClockDateCheck.IsCheckedChanged += OnClockChanged;
        FontSizeSlider.ValueChanged += OnAppearanceSliderChanged;
        OpacitySlider.ValueChanged += OnAppearanceSliderChanged;
        BgColorBox.TextChanged += (_, _) => PersistAppearance();
        TrayCapSlider.ValueChanged += OnTraySliderChanged;
        TrayIconSizeSlider.ValueChanged += OnTraySliderChanged;
        LockCheck.IsCheckedChanged += OnBehaviorChanged;
        AlwaysOnTopCheck.IsCheckedChanged += OnBehaviorChanged;
        ShowDesktopCheck.IsCheckedChanged += OnBehaviorChanged;
        GrantAccessibilityBtn.Click += OnGrantAccessibility;
        CloseBtn.Click += (_, _) => Close();
    }

    private void LoadSettings()
    {
        var s = _settings.Current;
        RunAtLoginCheck.IsChecked = s.RunAtLogin;

        WASNudge.IsChecked = s.WorkAreaStrategy == WorkAreaStrategy.Nudge;
        WASDockShim.IsChecked = s.WorkAreaStrategy == WorkAreaStrategy.DockShim;
        WASNone.IsChecked = s.WorkAreaStrategy == WorkAreaStrategy.None;

        // Taskbar button modes (bevel-m2.10). Set before the handlers are wired (ctor order), so
        // seeding these controls doesn't spuriously re-persist.
        var fixedWidth = s.TaskbarButtonWidthMode == TaskbarButtonWidthMode.Fixed;
        FixedWidthCheck.IsChecked = fixedWidth;
        MinWidthSlider.Value = s.TaskbarMinButtonWidth;
        MinWidthSlider.IsEnabled = !fixedWidth;   // min width only matters in shrink-to-fit
        MinWidthValue.Text = $"{s.TaskbarMinButtonWidth} px";

        GroupingCombo.SelectedIndex = (int)s.TaskbarGrouping;        // Never=0, WhenFull=1, Always=2
        LabelModeCombo.SelectedIndex = (int)s.TaskbarButtonLabels;   // Auto=0, Always=1, IconOnly=2
        MiddleClickCloseCheck.IsChecked = s.TaskbarMiddleClickCloses;
        ButtonSizeCombo.SelectedIndex = (int)s.TaskbarButtonSize;   // Small=0, Normal=1, Large=2

        ShowStartCheck.IsChecked = s.TaskbarShowStart;
        StartLabelBox.Text = s.TaskbarStartLabel;

        ShowClockCheck.IsChecked = s.TaskbarShowClock;
        Clock24Check.IsChecked = s.TaskbarClock24Hour;
        ClockSecondsCheck.IsChecked = s.TaskbarClockShowSeconds;
        ClockDateCheck.IsChecked = s.TaskbarClockShowDate;

        FontSizeSlider.Value = s.TaskbarFontSize > 0 ? s.TaskbarFontSize : 11;
        FontSizeValue.Text = $"{(int)FontSizeSlider.Value} pt";
        OpacitySlider.Value = s.TaskbarOpacity;
        OpacityValue.Text = $"{s.TaskbarOpacity} %";
        BgColorBox.Text = s.TaskbarBackgroundColor;

        TrayCapSlider.Value = s.TaskbarTrayOverflowCap;
        TrayCapValue.Text = $"{s.TaskbarTrayOverflowCap} icons";
        TrayIconSizeSlider.Value = s.TaskbarTrayIconSize;
        TrayIconSizeValue.Text = $"{s.TaskbarTrayIconSize} px";

        LockCheck.IsChecked = s.TaskbarLocked;
        AlwaysOnTopCheck.IsChecked = s.TaskbarAlwaysOnTop;
        ShowDesktopCheck.IsChecked = s.TaskbarShowDesktopButton;
    }

    private void StartPermissionPoll()
    {
        if (_permissionBroker is null) return;

        _permPollTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            async (_, _) => await PollPermissionAsync());
        _permPollTimer.Start();

        // Immediate first poll.
        _ = PollPermissionAsync();
    }

    private async Task PollPermissionAsync()
    {
        if (_permissionBroker is null) return;

        try
        {
            var state = await _permissionBroker.GetStateAsync(ShellPermission.Accessibility);
            Dispatcher.UIThread.Post(() => UpdatePermissionUI(state));
        }
        catch
        {
            // Best-effort; leave UI as-is.
        }
    }

    private void UpdatePermissionUI(PermissionState state)
    {
        switch (state)
        {
            case PermissionState.Granted:
                PermStatusDot.Background = Brushes.Green;
                PermStatusText.Text = "Granted";
                GrantAccessibilityBtn.IsVisible = false;
                PermHint.Text = "Window management is enabled.";
                break;
            case PermissionState.Denied:
                PermStatusDot.Background = Brushes.Red;
                PermStatusText.Text = "Not Granted";
                GrantAccessibilityBtn.IsVisible = true;
                PermHint.Text = "Grant Accessibility in System Settings to enable window management. No restart required.";
                break;
            default:
                PermStatusDot.Background = Brushes.Gray;
                PermStatusText.Text = "Unknown";
                GrantAccessibilityBtn.IsVisible = true;
                PermHint.Text = "Checking permission status...";
                break;
        }
    }

    private async void OnWorkAreaChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;

        var strategy = rb == WASNudge ? WorkAreaStrategy.Nudge
            : rb == WASDockShim ? WorkAreaStrategy.DockShim
            : WorkAreaStrategy.None;

        await _settings.UpdateAsync(s => s.WorkAreaStrategy = strategy);
    }

    private async void OnRunAtLoginChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressRunAtLogin) return;
        if (RunAtLoginCheck.IsChecked is not { } enabled) return;

        await _settings.UpdateAsync(s => s.RunAtLogin = enabled);

        // Actually (un)register with the OS (SMAppService via the PAL) — not just persist the flag.
        if (_shellSession is not null)
            await _shellSession.SetRunAtLoginAsync(enabled);
    }

    /// <summary>
    /// Reflects the REAL OS login-item state in the checkbox on open, so the toggle shows what will
    /// actually happen at next login rather than only the persisted preference. Best-effort: if the
    /// PAL can't answer (no ServiceManagement, not a signed bundle) the persisted value stands.
    /// </summary>
    private async Task ReconcileRunAtLoginAsync()
    {
        if (_shellSession is null) return;

        try
        {
            var osEnabled = await _shellSession.IsRunAtLoginEnabledAsync();
            Dispatcher.UIThread.Post(() =>
            {
                if (RunAtLoginCheck.IsChecked == osEnabled) return;
                _suppressRunAtLogin = true;
                RunAtLoginCheck.IsChecked = osEnabled;
                _suppressRunAtLogin = false;
            });
        }
        catch
        {
            // Best-effort; leave the checkbox on its persisted value.
        }
    }

    /// <summary>Persist a settings mutation and push it live, guarding the disk/SQLite I/O so a write
    /// failure logs instead of crashing the process out of one of these async-void handlers (review:
    /// reliability). Single seam for every dialog handler's persist-then-apply (was duplicated ~9x).</summary>
    private async System.Threading.Tasks.Task PersistAndApply(Action<BevelSettings> mutate)
    {
        try
        {
            await _settings.UpdateAsync(mutate);
            ApplyLive?.Invoke(_settings.Current);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[settings] persist failed (swallowed): {ex.Message}");
        }
    }

    private async void OnFixedWidthChanged(object? sender, RoutedEventArgs e)
    {
        if (FixedWidthCheck.IsChecked is not { } fixedWidth) return;
        MinWidthSlider.IsEnabled = !fixedWidth;
        var mode = fixedWidth ? TaskbarButtonWidthMode.Fixed : TaskbarButtonWidthMode.ShrinkToFit;
        await PersistAndApply(s => s.TaskbarButtonWidthMode = mode);
    }

    private async void OnMinWidthChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        MinWidthValue.Text = $"{value} px";
        // Persist only on an actual integer-step change, so a drag doesn't thrash the settings file.
        if (_settings.Current.TaskbarMinButtonWidth == value) return;
        await PersistAndApply(s => s.TaskbarMinButtonWidth = value);
    }

    private async void OnGroupingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GroupingCombo.SelectedIndex < 0) return;
        var mode = (TaskbarGroupingMode)GroupingCombo.SelectedIndex;
        if (_settings.Current.TaskbarGrouping == mode) return;
        await PersistAndApply(s => s.TaskbarGrouping = mode);
    }

    private async void OnLabelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LabelModeCombo.SelectedIndex < 0) return;
        var mode = (TaskbarButtonLabels)LabelModeCombo.SelectedIndex;
        if (_settings.Current.TaskbarButtonLabels == mode) return;
        await PersistAndApply(s => s.TaskbarButtonLabels = mode);
    }

    private async void OnMiddleClickChanged(object? sender, RoutedEventArgs e)
    {
        if (MiddleClickCloseCheck.IsChecked is not { } v) return;
        if (_settings.Current.TaskbarMiddleClickCloses == v) return;
        await PersistAndApply(s => s.TaskbarMiddleClickCloses = v);
    }

    /// <summary>Start show/caption both persist here (TextChanged fires per keystroke — the equality
    /// guard keeps it from thrashing the DB on no-op edits).</summary>
    private async void PersistStart()
    {
        var show = ShowStartCheck.IsChecked ?? true;
        var label = StartLabelBox.Text ?? "";
        if (_settings.Current.TaskbarShowStart == show && _settings.Current.TaskbarStartLabel == label) return;
        await PersistAndApply(s => { s.TaskbarShowStart = show; s.TaskbarStartLabel = label; });
    }

    private async void OnButtonSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ButtonSizeCombo.SelectedIndex < 0) return;
        var size = (TaskbarButtonSize)ButtonSizeCombo.SelectedIndex;
        if (_settings.Current.TaskbarButtonSize == size) return;
        await PersistAndApply(s => s.TaskbarButtonSize = size);
    }

    /// <summary>All four clock toggles funnel here: persist the set, then push it onto the live clock
    /// via <see cref="ApplyLive"/> so the change is visible immediately.</summary>
    private async void OnClockChanged(object? sender, RoutedEventArgs e)
    {
        var show = ShowClockCheck.IsChecked ?? true;
        var h24 = Clock24Check.IsChecked ?? true;
        var seconds = ClockSecondsCheck.IsChecked ?? false;
        var date = ClockDateCheck.IsChecked ?? false;

        await PersistAndApply(s =>
        {
            s.TaskbarShowClock = show;
            s.TaskbarClock24Hour = h24;
            s.TaskbarClockShowSeconds = seconds;
            s.TaskbarClockShowDate = date;
        });
    }

    private void OnAppearanceSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        FontSizeValue.Text = $"{(int)Math.Round(FontSizeSlider.Value)} pt";
        OpacityValue.Text = $"{(int)Math.Round(OpacitySlider.Value)} %";
        PersistAppearance();
    }

    /// <summary>Font size / opacity / tint all persist here (sliders + textbox fire continuously —
    /// the equality guard keeps a drag from thrashing the DB).</summary>
    private async void PersistAppearance()
    {
        var font = (int)Math.Round(FontSizeSlider.Value);
        var opacity = (int)Math.Round(OpacitySlider.Value);
        var color = BgColorBox.Text ?? "";
        if (_settings.Current.TaskbarFontSize == font
            && _settings.Current.TaskbarOpacity == opacity
            && _settings.Current.TaskbarBackgroundColor == color) return;
        await PersistAndApply(s =>
        {
            s.TaskbarFontSize = font;
            s.TaskbarOpacity = opacity;
            s.TaskbarBackgroundColor = color;
        });
    }

    private void OnTraySliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TrayCapValue.Text = $"{(int)Math.Round(TrayCapSlider.Value)} icons";
        TrayIconSizeValue.Text = $"{(int)Math.Round(TrayIconSizeSlider.Value)} px";
        PersistTray();
    }

    private async void PersistTray()
    {
        var cap = (int)Math.Round(TrayCapSlider.Value);
        var size = (int)Math.Round(TrayIconSizeSlider.Value);
        if (_settings.Current.TaskbarTrayOverflowCap == cap && _settings.Current.TaskbarTrayIconSize == size) return;
        await PersistAndApply(s => { s.TaskbarTrayOverflowCap = cap; s.TaskbarTrayIconSize = size; });
    }

    private async void OnBehaviorChanged(object? sender, RoutedEventArgs e)
    {
        var locked = LockCheck.IsChecked ?? false;
        var onTop = AlwaysOnTopCheck.IsChecked ?? true;
        var showDesktop = ShowDesktopCheck.IsChecked ?? false;
        if (_settings.Current.TaskbarLocked == locked
            && _settings.Current.TaskbarAlwaysOnTop == onTop
            && _settings.Current.TaskbarShowDesktopButton == showDesktop) return;
        await PersistAndApply(s =>
        {
            s.TaskbarLocked = locked;
            s.TaskbarAlwaysOnTop = onTop;
            s.TaskbarShowDesktopButton = showDesktop;
        });
    }

    private void OnGrantAccessibility(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
                UseShellExecute = true,
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _permPollTimer?.Stop();
        _permPollTimer = null;
    }
}