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
    private DispatcherTimer? _permPollTimer;

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

    public OnboardingWindow(SettingsService settings, IPermissionBroker? permissionBroker = null)
    {
        InitializeComponent();
        _settings = settings;
        _permissionBroker = permissionBroker;

        LoadSettings();
        StartPermissionPoll();

        WASNudge.IsCheckedChanged += OnWorkAreaChanged;
        WASDockShim.IsCheckedChanged += OnWorkAreaChanged;
        WASNone.IsCheckedChanged += OnWorkAreaChanged;
        RunAtLoginCheck.IsCheckedChanged += OnRunAtLoginChanged;
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
        if (RunAtLoginCheck.IsChecked is not { } enabled) return;
        await _settings.UpdateAsync(s => s.RunAtLogin = enabled);

        // LoginItemRegistrar integration (U10 full) — deferred until U9 is done.
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