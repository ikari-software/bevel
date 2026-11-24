using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using System.Linq;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
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
    private readonly ISettingsService _settings;
    private BevelSettings? _baseline;   // settings at open (or last Apply); Cancel reverts to this
    private readonly IPermissionBroker? _permissionBroker;
    private readonly IShellSession? _shellSession;
    private readonly IDockController? _dock;
    private readonly ISystemTrayHost? _tray;

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
    /// <summary>
    /// Hide the settings this platform cannot act on (bevel-platsettings). On Windows the dialog was
    /// offering macOS-only controls — "Accessibility Permission", "Screen Recording Permission", a
    /// Work-Area Strategy described as handling "the macOS Dock", and a tray page about mirroring "the
    /// macOS menu-bar icons". Every one of them is inert there, and an inert control is worse than a
    /// missing one: it asks the user to grant a permission that does not exist and reads as broken.
    ///
    /// <para>Gated on PAL CAPABILITY, not <c>OperatingSystem.Is*</c> — the same feature-detect contract
    /// the PAL states for its own callers. A permission the platform does not have reports
    /// <see cref="PermissionState.NotApplicable"/>; a Dock that is not there reports
    /// <c>Available: false</c>; and menu-bar consolidation only means anything where the tray is
    /// <see cref="TrayCapability.Mirrored"/> (macOS can only mirror someone else's bar — Windows shells
    /// own the tray protocol outright, so there is nothing to consolidate).</para>
    /// </summary>
    private void HidePlatformInapplicableSections()
    {
        // The Dock strategy exists only where a Dock does.
        if (_dock is not null && !_dock.Capabilities.Available)
            WorkAreaSection.IsVisible = false;

        // Consolidating a menu bar into the tray is meaningless unless the tray MIRRORS another bar.
        if (_tray is not null && _tray.Capabilities.TrayMode != TrayCapability.Mirrored)
            TraySection.IsVisible = false;

        if (_permissionBroker is null) return;

        // Permission state is async; resolve off the UI thread and hide on the dispatcher. Defaulting to
        // VISIBLE while unknown keeps macOS correct if the probe is slow — a briefly-shown real control
        // beats a permanently-hidden one. One row per ShellPermission (bevel-ct7).
        foreach (var (permission, row) in PermRows)
            _ = HideInapplicablePermissionAsync(permission, row);
    }

    /// <summary>Every permission row on the General tab, keyed by the permission it shows (bevel-ct7).</summary>
    private IReadOnlyDictionary<ShellPermission, PermissionRow> PermRows => _permRows ??= new Dictionary<ShellPermission, PermissionRow>
    {
        [ShellPermission.Accessibility] = PermRowAccessibility,
        [ShellPermission.ScreenRecording] = PermRowScreenRecording,
        [ShellPermission.FullDiskAccess] = PermRowFullDiskAccess,
        [ShellPermission.AppleEvents] = PermRowAppleEvents,
        [ShellPermission.InputMonitoring] = PermRowInputMonitoring,
    };
    private Dictionary<ShellPermission, PermissionRow>? _permRows;

    private async Task HideInapplicablePermissionAsync(ShellPermission permission, Control section)
    {
        PermissionState state;
        try { state = await _permissionBroker!.GetStateAsync(permission).ConfigureAwait(false); }
        catch { return; }   // a broker that cannot answer must not blank the section

        if (state != PermissionState.NotApplicable) return;
        await Dispatcher.UIThread.InvokeAsync(() => section.IsVisible = false);
    }

    public OnboardingWindow()
    {
        _settings = null!;
        InitializeComponent();
    }

    public OnboardingWindow(ISettingsService settings, IPermissionBroker? permissionBroker = null,
        IShellSession? shellSession = null, IDockController? dock = null, ISystemTrayHost? tray = null)
    {
        InitializeComponent();
        _settings = settings;
        _permissionBroker = permissionBroker;
        _shellSession = shellSession;
        _dock = dock;
        _tray = tray;
        HidePlatformInapplicableSections();

        foreach (var (_, display) in Bevel.UI.ThemeService.Themes)
            ThemeCombo.Items.Add(display);
        FontFamilyCombo.Items.Add("(Theme default)");
        foreach (var family in Bevel.UI.FontService.Families)
            FontFamilyCombo.Items.Add(family);
        LoadSettings();
        StartPermissionPoll();
        _ = ReconcileRunAtLoginAsync();
        ExportCrashLogsBtn.Click += OnExportCrashLogs;
        _ = RefreshCrashLogStateAsync();

        WASNudge.IsCheckedChanged += OnWorkAreaChanged;
        WASDockShim.IsCheckedChanged += OnWorkAreaChanged;
        WASNone.IsCheckedChanged += OnWorkAreaChanged;
        RunAtLoginCheck.IsCheckedChanged += OnRunAtLoginChanged;
        FixedWidthCheck.IsCheckedChanged += OnFixedWidthChanged;
        MinWidthSlider.ValueChanged += OnMinWidthChanged;
        GroupingCombo.SelectionChanged += OnGroupingChanged;
        LabelModeCombo.SelectionChanged += OnLabelModeChanged;
        SortModeCombo.SelectionChanged += OnSortModeChanged;
        WindowlessLastCheck.IsCheckedChanged += OnWindowlessLastChanged;
        MiddleClickCloseCheck.IsCheckedChanged += OnMiddleClickChanged;
        ReclickMinimizeCombo.SelectionChanged += OnReclickMinimizeChanged;
        ShowStartCheck.IsCheckedChanged += (_, _) => PersistStart();
        StartLabelBox.TextChanged += (_, _) => PersistStart();
        FrequentCountSlider.ValueChanged += OnFrequentCountChanged;
        ButtonSizeCombo.SelectionChanged += OnButtonSizeChanged;
        RowsSlider.ValueChanged += OnRowsChanged;
        CrispBevelsCheck.IsCheckedChanged += OnCrispBevelsChanged;
        StartBadgeDetailCheck.IsCheckedChanged += OnStartBadgeDetailChanged;
        ShowClockCheck.IsCheckedChanged += OnClockChanged;
        Clock24Check.IsCheckedChanged += OnClockChanged;
        ClockSecondsCheck.IsCheckedChanged += OnClockChanged;
        ClockDateCheck.IsCheckedChanged += OnClockChanged;
        FontSizeSlider.ValueChanged += OnAppearanceSliderChanged;
        OpacitySlider.ValueChanged += OnAppearanceSliderChanged;
        BgColorBox.TextChanged += (_, _) => PersistAppearance();
        ThemeCombo.SelectionChanged += OnThemeChanged;
        PreviewThemeButton.Click += OnPreviewTheme;
        FontFamilyCombo.SelectionChanged += OnFontFamilyChanged;
        TrayCapSlider.ValueChanged += OnTraySliderChanged;
        TrayIconSizeSlider.ValueChanged += OnTraySliderChanged;
        ConsolidateCheck.IsCheckedChanged += OnConsolidateChanged;
        LockCheck.IsCheckedChanged += OnBehaviorChanged;
        AlwaysOnTopCheck.IsCheckedChanged += OnBehaviorChanged;
        ShowDesktopCheck.IsCheckedChanged += OnBehaviorChanged;
        foreach (var (permission, row) in PermRows)
            row.GrantRequested += (_, _) => OnGrantPermission(permission);
        UpdateIntervalHours.ValueChanged += OnUpdateIntervalChanged;

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
            Bevel.UI.ThemeVariants.Apply(b);
            Bevel.UI.FontService.Apply(b.UiFontFamily);
            ApplyLive?.Invoke(_settings.Current);
            BuildThemeOptions();   // resync the subpanel to the reverted theme + options
        }
        Close();
    }

    private void LoadSettings()
    {
        var s = _settings.Current;
        RunAtLoginCheck.IsChecked = s.RunAtLogin;
        UpdateIntervalHours.Value = s.UpdateCheckIntervalHours;
        RefreshUpdateStatus();

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
        SortModeCombo.SelectedIndex = (int)s.TaskbarWindowSort;      // OpenOrder=0, Name=1
        WindowlessLastCheck.IsChecked = s.WindowlessAppsLast;
        MiddleClickCloseCheck.IsChecked = s.TaskbarMiddleClickCloses;
        // The chord is named in the host platform's vocabulary (Option on macOS, Alt on Windows) — the
        // live-modifier probe behind it is per-platform too (bevel-au94).
        ReclickModifierItem.Content = $"Minimizes with {TaskButtonClickPolicy.ModifierName} held";
        ReclickMinimizeCombo.SelectedIndex = (int)s.TaskbarReclickMinimize;   // Click=0, OptionClick=1, Never=2
        ButtonSizeCombo.SelectedIndex = (int)s.TaskbarButtonSize;   // Small=0, Normal=1, Large=2, Big=3
        RowsSlider.Value = s.TaskbarRows;
        RowsValue.Text = $"{s.TaskbarRows} row{(s.TaskbarRows == 1 ? "" : "s")}";
        RefreshCrispBevels();
        StartBadgeDetailCheck.IsChecked = s.StartBadgeFullDetail;

        ShowStartCheck.IsChecked = s.TaskbarShowStart;
        StartLabelBox.Text = s.TaskbarStartLabel;
        FrequentCountSlider.Value = s.TaskbarStartMenuFrequentCount;
        FrequentCountValue.Text = $"{s.TaskbarStartMenuFrequentCount} programs";

        ShowClockCheck.IsChecked = s.TaskbarShowClock;
        Clock24Check.IsChecked = s.TaskbarClock24Hour;
        ClockSecondsCheck.IsChecked = s.TaskbarClockShowSeconds;
        ClockDateCheck.IsChecked = s.TaskbarClockShowDate;

        ThemeCombo.SelectedIndex = ThemeIndex(s.ThemeId);
        BuildThemeOptions();
        FontFamilyCombo.SelectedIndex = FontIndex(s.UiFontFamily);
        FontSizeSlider.Value = s.TaskbarFontSize > 0 ? s.TaskbarFontSize : 11;
        FontSizeValue.Text = $"{(int)FontSizeSlider.Value} pt";
        OpacitySlider.Value = s.TaskbarOpacity;
        OpacityValue.Text = $"{s.TaskbarOpacity} %";
        BgColorBox.Text = s.TaskbarBackgroundColor;

        ConsolidateCheck.IsChecked = s.TaskbarConsolidateMenuBar;
        TrayCapSlider.Value = s.TaskbarTrayOverflowCap;
        TrayCapValue.Text = $"{s.TaskbarTrayOverflowCap} boxes / row";
        TrayIconSizeSlider.Value = s.TaskbarTrayIconSize;
        TrayIconSizeValue.Text = TrayScaleLabel(s.TaskbarTrayIconSize);

        LockCheck.IsChecked = s.TaskbarLocked;
        AlwaysOnTopCheck.IsChecked = s.TaskbarAlwaysOnTop;
        ShowDesktopCheck.IsChecked = s.TaskbarShowDesktopButton;
    }

    private void StartPermissionPoll()
    {
        if (_permissionBroker is null) return;

        // bevel-sdss: the broker owns the poll (subscriber-gated inside the broker) and raises
        // PermissionChanged when a state flips — subscribe instead of running a private timer.
        _permissionBroker.PermissionChanged += OnPermissionChanged;

        // Immediate first poll.
        _ = PollPermissionAsync();
    }

    private void OnPermissionChanged(object? sender, ShellPermission permission)
        => Dispatcher.UIThread.Post(() => _ = PollPermissionAsync());

    private async Task PollPermissionAsync()
    {
        if (_permissionBroker is null) return;

        try
        {
            // Query every permission off the UI thread, then post all results back (bevel-ct7).
            var states = new Dictionary<ShellPermission, PermissionState>();
            foreach (var permission in PermRows.Keys)
                states[permission] = await _permissionBroker.GetStateAsync(permission).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (permission, state) in states)
                    PermRows[permission].ApplyState(state, GrantedHint(permission), DeniedHint(permission));
            });
        }
        catch
        {
            // Best-effort; leave UI as-is.
        }
    }

    private static string GrantedHint(ShellPermission p) => p switch
    {
        ShellPermission.Accessibility => "Window management is enabled.",
        ShellPermission.ScreenRecording => "Live window titles and tray icon mirroring are enabled.",
        ShellPermission.FullDiskAccess => "The Filer can list and organize files across your disks.",
        ShellPermission.AppleEvents => "Other apps can drive Bevel (bevel:// automation).",
        ShellPermission.InputMonitoring => "The idle Ctrl/Option+Esc hotkey works.",
        _ => "",
    };

    private static string DeniedHint(ShellPermission p) => p switch
    {
        ShellPermission.Accessibility => "Grant Accessibility (to BevelHelper) in System Settings to enable window management. No restart required.",
        ShellPermission.ScreenRecording => "Grant Screen Recording (to BevelHelper) for live window titles and tray icon mirroring. Takes effect after a relaunch.",
        ShellPermission.FullDiskAccess => "Grant Full Disk Access (to BevelHelper) so the Filer can read every folder it shows.",
        ShellPermission.AppleEvents => "Grant Apple Events (to BevelHelper) so other apps can script Bevel.",
        ShellPermission.InputMonitoring => "Grant Input Monitoring (to BevelHelper) for the idle Ctrl/Option+Esc hotkey.",
        _ => "Checking permission status...",
    };

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

    // ── Diagnostics: durable crash-log export (bevel-tak) ────────────────

    /// <summary>Sets the export button's enabled state + status line from the crash-log dir, off the
    /// UI thread. "No crashes recorded" is a real state, shown as disabled-with-text — not a hidden
    /// control.</summary>
    private async Task RefreshCrashLogStateAsync()
    {
        var count = await Task.Run(() => Bevel.Core.Diagnostics.CrashLog.Count(Bevel.Core.Diagnostics.CrashLog.DefaultDir));
        Dispatcher.UIThread.Post(() =>
        {
            ExportCrashLogsBtn.IsEnabled = count > 0;
            CrashLogStatus.Text = count > 0
                ? $"{count} crash log{(count == 1 ? "" : "s")} recorded — export them for a bug report."
                : "No crashes recorded";
        });
    }

    private async void OnExportCrashLogs(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        var file = top is null ? null : await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export crash logs",
            SuggestedFileName = $"Bevel-crash-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            FileTypeChoices = new[] { new FilePickerFileType("Zip archive") { Patterns = new[] { "*.zip" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        ExportCrashLogsBtn.IsEnabled = false;
        try
        {
            // Crash files live in the durable log dir; the restart/filer logs are the temp extras.
            var extras = new List<string> { Path.Combine(Path.GetTempPath(), "bevel-restart.log") };
            try { extras.AddRange(Directory.GetFiles(Path.GetTempPath(), "bevel-filer-*.log")); } catch { }
            await Task.Run(() => Bevel.Core.Diagnostics.CrashLog.ExportAsync(Bevel.Core.Diagnostics.CrashLog.DefaultDir, path, extras));
            CrashLogStatus.Text = $"Exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            CrashLogStatus.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            ExportCrashLogsBtn.IsEnabled = true;
        }
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

    private async void OnSortModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SortModeCombo.SelectedIndex < 0) return;
        var mode = (TaskbarWindowSort)SortModeCombo.SelectedIndex;
        if (_settings.Current.TaskbarWindowSort == mode) return;
        await PersistAndApply(s => s.TaskbarWindowSort = mode);
    }

    private async void OnWindowlessLastChanged(object? sender, RoutedEventArgs e)
    {
        if (WindowlessLastCheck.IsChecked is not { } v) return;
        if (_settings.Current.WindowlessAppsLast == v) return;
        await PersistAndApply(s => s.WindowlessAppsLast = v);
    }

    private async void OnLabelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LabelModeCombo.SelectedIndex < 0) return;
        var mode = (TaskbarButtonLabels)LabelModeCombo.SelectedIndex;
        if (_settings.Current.TaskbarButtonLabels == mode) return;
        await PersistAndApply(s => s.TaskbarButtonLabels = mode);
    }

    private async void OnStartBadgeDetailChanged(object? sender, RoutedEventArgs e)
    {
        if (StartBadgeDetailCheck.IsChecked is not { } v) return;
        if (_settings.Current.StartBadgeFullDetail == v) return;
        await PersistAndApply(s => s.StartBadgeFullDetail = v);
    }

    private async void OnMiddleClickChanged(object? sender, RoutedEventArgs e)
    {
        if (MiddleClickCloseCheck.IsChecked is not { } v) return;
        if (_settings.Current.TaskbarMiddleClickCloses == v) return;
        await PersistAndApply(s => s.TaskbarMiddleClickCloses = v);
    }

    /// <summary>Reclick-minimize mode (bevel-au94). Applies live: TaskbarView pushes the new mode into
    /// <see cref="TaskButtonClickPolicy.Shared"/> on the next settings poll, in this process and in peers.</summary>
    private async void OnReclickMinimizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReclickMinimizeCombo.SelectedIndex < 0) return;
        var mode = (TaskbarReclickMinimize)ReclickMinimizeCombo.SelectedIndex;
        if (_settings.Current.TaskbarReclickMinimize == mode) return;
        await PersistAndApply(s => s.TaskbarReclickMinimize = mode);
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
        // "Big icons" is the Win10/11 preset, so picking it also turns labels off (bevel-c54t) — a big
        // glyph next to a title is neither look. The two settings stay INDEPENDENT (the user can turn
        // labels back on from the mode combo right below); this only pairs them at the moment of choice.
        var pairIconOnly = size == TaskbarButtonSize.Big
                           && _settings.Current.TaskbarButtonLabels != TaskbarButtonLabels.IconOnly;
        await PersistAndApply(s =>
        {
            s.TaskbarButtonSize = size;
            if (pairIconOnly) s.TaskbarButtonLabels = TaskbarButtonLabels.IconOnly;
        });
        // Reflect the paired change in the label combo. Its handler re-reads the already-persisted
        // value and no-ops, so this can't double-write.
        if (pairIconOnly) LabelModeCombo.SelectedIndex = (int)TaskbarButtonLabels.IconOnly;
    }

    private async void OnRowsChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var rows = (int)Math.Round(RowsSlider.Value);
        RowsValue.Text = $"{rows} row{(rows == 1 ? "" : "s")}";
        if (_settings.Current.TaskbarRows == rows) return;
        await PersistAndApply(s => s.TaskbarRows = rows);   // ApplyLiveSettings calls _window.SetRows
    }

    private bool _refreshingCrisp;
    /// <summary>Crisp-bevels is a PER-THEME override, so the checkbox reflects the ACTIVE theme's value and
    /// is re-read on load and whenever the theme changes (set without firing the change handler).</summary>
    private void RefreshCrispBevels()
    {
        _refreshingCrisp = true;
        CrispBevelsCheck.IsChecked = _settings.ThemeOverridesFor(_settings.Current.ThemeId).CrispBevels ?? false;
        _refreshingCrisp = false;
    }

    private async void OnCrispBevelsChanged(object? sender, RoutedEventArgs e)
    {
        if (_refreshingCrisp) return;
        var crisp = CrispBevelsCheck.IsChecked == true;
        var theme = _settings.Current.ThemeId;
        if (Application.Current is { } app) Bevel.UI.ThemeOptions.ApplyCrispBevels(app, crisp);   // live
        try { await _settings.UpdateThemeOverridesAsync(theme, o => o.CrispBevels = crisp); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] crisp-bevels persist failed: {ex.Message}"); }
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

    private async void OnPreviewTheme(object? sender, RoutedEventArgs e)
    {
        var idx = ThemeCombo.SelectedIndex;
        if (idx < 0 || idx >= Bevel.UI.ThemeService.Themes.Count) return;
        var selected = Bevel.UI.ThemeService.Themes[idx].Id;
        var previous = Bevel.UI.ThemeService.Current;
        if (!Bevel.UI.ThemeService.Apply(selected)) return;

        var preview = new ThemePreviewWindow();
        preview.Closed += (_, _) =>
        {
            if (!string.Equals(previous, selected, StringComparison.Ordinal))
                Bevel.UI.ThemeService.Apply(previous);
        };
        var owner = this;
        await preview.ShowDialog(owner);
    }

    /// <summary>Theme picker (PKG-03): swaps the whole token bundle live via
    /// <see cref="Bevel.UI.ThemeService"/>, then persists the choice (guarded).</summary>
    private async void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = ThemeCombo.SelectedIndex;
        if (idx < 0 || idx >= Bevel.UI.ThemeService.Themes.Count) return;
        var id = Bevel.UI.ThemeService.Themes[idx].Id;
        if (_settings.Current.ThemeId == id) return;
        var pos = CaptureDialogPosition();
        Bevel.UI.ThemeService.Apply(id);   // live reskin
        try { await _settings.UpdateAsync(s => s.ThemeId = id); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] theme persist failed: {ex.Message}"); }
        // Apply the new theme's own appearance variant (colour scheme / colour+gloss) and clear the
        // others, then rebuild the options subpanel so it shows what THIS theme contributes.
        Bevel.UI.ThemeVariants.Apply(_settings.Current);
        BuildThemeOptions();
        RefreshCrispBevels();   // per-theme override: reflect the new theme's value
        RestoreDialogPosition(pos);   // keep the user on the Appearance tab (theming in flight)
    }

    /// <summary>Selected tab + active scroll offset, captured before a theme/variant apply.</summary>
    private (int Tab, Vector Offset) CaptureDialogPosition()
    {
        var tabs = this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        var sv = tabs?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible);
        return (tabs?.SelectedIndex ?? -1, sv?.Offset ?? default);
    }

    /// <summary>Restores the tab + scroll after a theme/variant apply. ThemeService.Apply swaps the theme's
    /// Styles set, which re-templates the whole dialog and resets the selected tab + scroll — so live
    /// theming would kick the user off the Appearance tab. Restore once the re-layout settles so the theme
    /// applies "in flight", leaving the user exactly where they were.</summary>
    private void RestoreDialogPosition((int Tab, Vector Offset) pos)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tabs = this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            if (tabs != null && pos.Tab >= 0 && pos.Tab < tabs.ItemCount) tabs.SelectedIndex = pos.Tab;
            var sv = tabs?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible);
            if (sv != null) sv.Offset = pos.Offset;
        }, DispatcherPriority.Background);   // after the re-template/layout resets, so our restore wins
    }

    /// <summary>Builds the theme-specific appearance subpanel from <see cref="Bevel.UI.ThemeVariants"/>:
    /// one labelled combo per option the active theme contributes. Rebuilt on load, theme change, and
    /// Cancel-revert, so the panel always reflects the active theme's capabilities (Win2000 = colour
    /// scheme; Luna = colour + gloss; a theme that offers none shows an empty panel).</summary>
    private void BuildThemeOptions()
    {
        ThemeOptionsPanel.Children.Clear();
        foreach (var option in Bevel.UI.ThemeVariants.OptionsFor(_settings.Current.ThemeId))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = option.Label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 84 });
            var combo = new ComboBox { FontSize = 11, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (_, display) in option.Choices) combo.Items.Add(display);
            combo.SelectedIndex = option.CurrentIndex(_settings.Current);
            combo.SelectionChanged += (_, _) => OnThemeOptionChanged(option, combo);
            row.Children.Add(combo);
            ThemeOptionsPanel.Children.Add(row);
        }
    }

    /// <summary>A theme-variant option changed: persist the choice on its settings field, then apply the
    /// active theme's variant live in this process (other roles pick it up via settings.Changed).</summary>
    private async void OnThemeOptionChanged(Bevel.UI.ThemeOption option, ComboBox combo)
    {
        var idx = combo.SelectedIndex;
        if (idx < 0 || idx >= option.Choices.Count) return;
        var id = option.Choices[idx].Id;
        if (option.Get(_settings.Current) == id) return;
        var pos = CaptureDialogPosition();
        try { await _settings.UpdateAsync(s => option.Set(s, id)); }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] theme option persist failed: {ex.Message}"); return; }
        Bevel.UI.ThemeVariants.Apply(_settings.Current);   // live recolour in this process
        RestoreDialogPosition(pos);
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

    private async void OnConsolidateChanged(object? sender, RoutedEventArgs e)
    {
        var on = ConsolidateCheck.IsChecked ?? false;
        if (_settings.Current.TaskbarConsolidateMenuBar == on) return;
        // Persist; TaskbarViewModel's settings subscription drives the actual hide/reveal (U8).
        await PersistAndApply(s => s.TaskbarConsolidateMenuBar = on);
    }

    private void OnTraySliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TrayCapValue.Text = $"{(int)Math.Round(TrayCapSlider.Value)} boxes / row";
        TrayIconSizeValue.Text = TrayScaleLabel((int)Math.Round(TrayIconSizeSlider.Value));
        PersistTray();
    }

    /// <summary>The tray icon-size slider scales the NATIVE macOS size uniformly; 16 == native (bevel-7hf4).
    /// Show "Native" at the baseline so the safe default reads clearly, a ×multiplier otherwise.</summary>
    private static string TrayScaleLabel(int size) => size == 16 ? "Native" : $"{size / 16.0:0.##}×";

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

    private void OnGrantPermission(ShellPermission permission)
    {
        if (!OperatingSystem.IsMacOS()) return;

        var pane = permission switch
        {
            ShellPermission.Accessibility => "Privacy_Accessibility",
            ShellPermission.ScreenRecording => "Privacy_ScreenCapture",
            ShellPermission.FullDiskAccess => "Privacy_AllFiles",
            ShellPermission.AppleEvents => "Privacy_AppleEvents",
            ShellPermission.InputMonitoring => "Privacy_ListenEvent",
            _ => "Privacy",
        };
        Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            Arguments = $"x-apple.systempreferences:com.apple.preference.security?{pane}",
            UseShellExecute = true,
        });
    }

    // ── Updates (bevel-ct7): cadence only; the feed URL stays owner-controlled ──

    private async void OnUpdateIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (UpdateIntervalHours.Value is not { } v) return;

        // Clamp through the policy so a mistyped value cannot become a hot loop (1h floor, 1 week cap).
        var clamped = (int)Bevel.Core.Updates.UpdateCheckPolicy.ClampInterval((int)v).TotalHours;
        await _settings.UpdateAsync(s => s.UpdateCheckIntervalHours = clamped);
        RefreshUpdateStatus();
    }

    private void RefreshUpdateStatus()
    {
        var s = _settings.Current;
        UpdateStatusText.Text = Bevel.Core.Updates.UpdateCheckPolicy.IsEnabled(s.UpdateFeedUrl)
            ? $"Update checks run every {Bevel.Core.Updates.UpdateCheckPolicy.ClampInterval(s.UpdateCheckIntervalHours).TotalHours:0} h. Updates apply on the next restart."
            : "Automatic update checks are off in this build.";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_permissionBroker is not null)
            _permissionBroker.PermissionChanged -= OnPermissionChanged;
    }
}