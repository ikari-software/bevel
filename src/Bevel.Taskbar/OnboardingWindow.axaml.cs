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
public partial class OnboardingWindow : Bevel.UI.BevelWindow
{
    private readonly SettingsService _settings;
    private BevelSettings? _baseline;   // settings at open (or last Apply); Cancel reverts to this
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

        foreach (var (_, display) in Bevel.UI.ThemeService.Themes)
            ThemeCombo.Items.Add(display);
        foreach (var (_, display) in Bevel.UI.ColorSchemeService.Schemes)
            ColorSchemeCombo.Items.Add(display);
        FontFamilyCombo.Items.Add("(Theme default)");
        foreach (var family in Bevel.UI.FontService.Families)
            FontFamilyCombo.Items.Add(family);
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
        FrequentCountSlider.ValueChanged += OnFrequentCountChanged;
        ButtonSizeCombo.SelectionChanged += OnButtonSizeChanged;
        ShowClockCheck.IsCheckedChanged += OnClockChanged;
        Clock24Check.IsCheckedChanged += OnClockChanged;
        ClockSecondsCheck.IsCheckedChanged += OnClockChanged;
        ClockDateCheck.IsCheckedChanged += OnClockChanged;
        FontSizeSlider.ValueChanged += OnAppearanceSliderChanged;
        OpacitySlider.ValueChanged += OnAppearanceSliderChanged;
        BgColorBox.TextChanged += (_, _) => PersistAppearance();
        ThemeCombo.SelectionChanged += OnThemeChanged;
        ColorSchemeCombo.SelectionChanged += OnColorSchemeChanged;
        FontFamilyCombo.SelectionChanged += OnFontFamilyChanged;
        TrayCapSlider.ValueChanged += OnTraySliderChanged;
        TrayIconSizeSlider.ValueChanged += OnTraySliderChanged;
        LockCheck.IsCheckedChanged += OnBehaviorChanged;
        AlwaysOnTopCheck.IsCheckedChanged += OnBehaviorChanged;
        ShowDesktopCheck.IsCheckedChanged += OnBehaviorChanged;
        GrantAccessibilityBtn.Click += OnGrantAccessibility;

        // OK / Cancel / Apply. Snapshot now (state is clean here — handlers just got wired, no changes yet).
        _baseline = _settings.Current.Clone();
        OkBtn.Click += (_, _) => Close();
        CancelBtn.Click += OnCancel;
        ApplyBtn.Click += OnApply;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Any settings mutation dirties the sheet, enabling Apply. Marshalled to the UI thread since
    /// Changed can be raised off it (persist runs async).</summary>
    private void OnSettingsChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyBtn.IsEnabled = true);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        _baseline = _settings.Current.Clone();   // commit current as the new revert baseline
        ApplyBtn.IsEnabled = false;
    }

    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_baseline is { } b)
        {
            try { await _settings.UpdateAsync(s => s.CopyFrom(b)); }
            catch (Exception ex) { Console.Error.WriteLine($"[settings] cancel revert failed: {ex.Message}"); }
            // Re-apply the live visual services from the snapshot (they aren't driven by ApplyLive).
            Bevel.UI.ThemeService.Apply(b.ThemeId);
            Bevel.UI.ColorSchemeService.Apply(b.ColorScheme);
            Bevel.UI.FontService.Apply(b.UiFontFamily);
            ApplyLive?.Invoke(_settings.Current);
        }
        Close();
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
        FrequentCountSlider.Value = s.TaskbarStartMenuFrequentCount;
        FrequentCountValue.Text = $"{s.TaskbarStartMenuFrequentCount} programs";

        ShowClockCheck.IsChecked = s.TaskbarShowClock;
        Clock24Check.IsChecked = s.TaskbarClock24Hour;
        ClockSecondsCheck.IsChecked = s.TaskbarClockShowSeconds;
        ClockDateCheck.IsChecked = s.TaskbarClockShowDate;

        ThemeCombo.SelectedIndex = ThemeIndex(s.ThemeId);
        ColorSchemeCombo.SelectedIndex = SchemeIndex(s.ColorScheme);
        FontFamilyCombo.SelectedIndex = FontIndex(s.UiFontFamily);
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
                PermStatusDot.Background = new SolidColorBrush(Color.Parse("#3FA23F"));
                PermStatusGlyph.Data = Geometry.Parse("M0,3.5 L2.6,6 L7,0.5");   // check
                PermStatusText.Text = "Granted";
                GrantAccessibilityBtn.IsVisible = false;
                PermHint.Text = "Window management is enabled.";
                break;
            case PermissionState.Denied:
                PermStatusDot.Background = new SolidColorBrush(Color.Parse("#E24A2E"));
                PermStatusGlyph.Data = Geometry.Parse("M0,0 L6,6 M6,0 L0,6");     // cross
                PermStatusText.Text = "Not Granted";
                GrantAccessibilityBtn.IsVisible = true;
                PermHint.Text = "Grant Accessibility in System Settings to enable window management. No restart required.";
                break;
            default:
                PermStatusDot.Background = new SolidColorBrush(Color.Parse("#9AA0A6"));
                PermStatusGlyph.Data = Geometry.Parse("M0,3 L7,3");                // dash
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

    private void OnFrequentCountChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        FrequentCountValue.Text = $"{(int)Math.Round(FrequentCountSlider.Value)} programs";
        PersistStartMenu();
    }

    private async void PersistStartMenu()
    {
        var count = (int)Math.Round(FrequentCountSlider.Value);
        if (_settings.Current.TaskbarStartMenuFrequentCount == count) return;
        // PersistAndApply raises settings.Changed, which App wires to ShellModel.FrequentCap — so the
        // Start menu's left column re-caps live, no restart.
        await PersistAndApply(s => s.TaskbarStartMenuFrequentCount = count);
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

    private static int SchemeIndex(string id)
    {
        var target = string.IsNullOrEmpty(id) ? Bevel.UI.ColorSchemeService.DefaultScheme : id;
        for (var i = 0; i < Bevel.UI.ColorSchemeService.Schemes.Count; i++)
            if (Bevel.UI.ColorSchemeService.Schemes[i].Id == target) return i;
        return 0;
    }

    private static int ThemeIndex(string id)
    {
        var target = string.IsNullOrEmpty(id) ? Bevel.UI.ThemeService.DefaultTheme : id;
        for (var i = 0; i < Bevel.UI.ThemeService.Themes.Count; i++)
            if (Bevel.UI.ThemeService.Themes[i].Id == target) return i;
        return 0;
    }

    // Picker index 0 is the synthetic "(Theme default)" entry; installed families follow at 1…N.
    private static int FontIndex(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return 0;
        for (var i = 0; i < Bevel.UI.FontService.Families.Count; i++)
            if (string.Equals(Bevel.UI.FontService.Families[i], family, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return 0;   // a family that's no longer installed falls back to the theme default
    }

    /// <summary>Theme picker (PKG-03): swaps the whole token bundle live via
    /// <see cref="Bevel.UI.ThemeService"/>, then persists the choice (guarded).</summary>
    private async void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = ThemeCombo.SelectedIndex;
        if (idx < 0 || idx >= Bevel.UI.ThemeService.Themes.Count) return;
        var id = Bevel.UI.ThemeService.Themes[idx].Id;
        if (_settings.Current.ThemeId == id) return;
        Bevel.UI.ThemeService.Apply(id);   // live reskin
        try { await _settings.UpdateAsync(s => s.ThemeId = id); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] theme persist failed: {ex.Message}"); }
    }

    /// <summary>UI font-family picker (FNT-01): reskins the shell font live via
    /// <see cref="Bevel.UI.FontService"/>, then persists the choice (guarded).</summary>
    private async void OnFontFamilyChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = FontFamilyCombo.SelectedIndex;
        if (idx < 0) return;
        var family = idx == 0 ? "" : Bevel.UI.FontService.Families[idx - 1];
        if (_settings.Current.UiFontFamily == family) return;
        Bevel.UI.FontService.Apply(family);   // live reskin
        try { await _settings.UpdateAsync(s => s.UiFontFamily = family); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] font persist failed: {ex.Message}"); }
    }

    /// <summary>Win2000 colour-scheme picker (bevel-9js): recolours the whole shell live via
    /// <see cref="Bevel.UI.ColorSchemeService"/>, then persists the choice (guarded).</summary>
    private async void OnColorSchemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = ColorSchemeCombo.SelectedIndex;
        if (idx < 0 || idx >= Bevel.UI.ColorSchemeService.Schemes.Count) return;
        var id = Bevel.UI.ColorSchemeService.Schemes[idx].Id;
        if (_settings.Current.ColorScheme == id) return;
        Bevel.UI.ColorSchemeService.Apply(id);   // live recolour
        try { await _settings.UpdateAsync(s => s.ColorScheme = id); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] colour scheme persist failed: {ex.Message}"); }
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