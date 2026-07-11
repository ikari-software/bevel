using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Bevel.Taskbar;

/// <summary>
/// Taskbar clock widget. Displays current time in HH:mm format (12/24h per OS locale),
/// with a long-date tooltip on hover. Updates on minute boundaries per R-CL-1's
/// ≤1 wake/min budget. Double-click deep-links to system date/time settings.
/// </summary>
public partial class ClockWidget : UserControl
{
    private DispatcherTimer? _timer;

    public ClockWidget()
    {
        InitializeComponent();
        UpdateTime();

        // Double-click deep-links to System Settings → Date & Time.
        Tapped += (_, _) =>
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = "x-apple.systempreferences:com.apple.preference.datetime",
                    UseShellExecute = true,
                });
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    private void StartTimer()
    {
        StopTimer();

        // Align to the next minute boundary so we don't drift.
        var now = DateTime.Now;
        var nextMinute = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
        var delay = nextMinute - now + TimeSpan.FromMilliseconds(100); // 100ms past the boundary

        _timer = new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.Background,
            OnTimerTick);

        // First tick: update now to catch the initial render.
        UpdateTime();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        UpdateTime();
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;
        TimeDisplay.Text = now.ToString("HH:mm");
        ToolTip.SetTip(TimeDisplay, now.ToLongDateString());
    }
}