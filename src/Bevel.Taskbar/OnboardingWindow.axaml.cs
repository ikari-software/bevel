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
        GroupWindowsCheck.IsCheckedChanged += OnGroupWindowsChanged;
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

        GroupWindowsCheck.IsChecked = s.TaskbarGroupWindows;
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

    private async void OnFixedWidthChanged(object? sender, RoutedEventArgs e)
    {
        if (FixedWidthCheck.IsChecked is not { } fixedWidth) return;
        MinWidthSlider.IsEnabled = !fixedWidth;
        var mode = fixedWidth ? TaskbarButtonWidthMode.Fixed : TaskbarButtonWidthMode.ShrinkToFit;
        await _settings.UpdateAsync(s => s.TaskbarButtonWidthMode = mode);
    }

    private async void OnMinWidthChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        MinWidthValue.Text = $"{value} px";
        // Persist only on an actual integer-step change, so a drag doesn't thrash the settings file.
        if (_settings.Current.TaskbarMinButtonWidth == value) return;
        await _settings.UpdateAsync(s => s.TaskbarMinButtonWidth = value);
    }

    private async void OnGroupWindowsChanged(object? sender, RoutedEventArgs e)
    {
        if (GroupWindowsCheck.IsChecked is not { } group) return;
        await _settings.UpdateAsync(s => s.TaskbarGroupWindows = group);
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