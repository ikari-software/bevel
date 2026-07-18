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

    // Display settings (bevel-cust.clock). Defaults mirror the original hardcoded "HH:mm" behaviour,
    // so an un-Configure'd widget (XAML previewer, tests) renders exactly as before.
    private bool _show = true;
    private bool _h24 = true;
    private bool _seconds;
    private bool _showDate;

    public ClockWidget()
    {
        InitializeComponent();

        // Anchor the tooltip ABOVE the clock, not at the pointer (Avalonia's default). The taskbar
        // sits at the screen bottom, so a pointer-placed tooltip lands under the cursor — moving onto
        // it fires PointerExited on the clock, which hides the tooltip, which puts the cursor back on
        // the clock, which reshows it: a flicker loop. Top placement keeps it clear of the cursor,
        // matching the task-button tooltips' Placement=Top (bevel-nji follow-up).
        ToolTip.SetPlacement(TimeDisplay, PlacementMode.Top);
        ToolTip.SetVerticalOffset(TimeDisplay, -2);

        UpdateTime();

        // Double-click (or right-click → Adjust Date & Time) deep-links to System Settings.
        Tapped += (_, _) => OpenDateTimeSettings();
    }

    private void OnAdjustDateTime(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenDateTimeSettings();

    private static void OpenDateTimeSettings()
    {
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "x-apple.systempreferences:com.apple.preference.datetime",
                UseShellExecute = true,
            });
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

    /// <summary>Apply clock display settings (bevel-cust.clock). Rebuilds the format and, when seconds
    /// are shown, switches the tick cadence from minute- to second-boundary. Safe to call live — the
    /// running taskbar's clock reformats immediately.</summary>
    public void Configure(bool show, bool h24, bool seconds, bool showDate)
    {
        _show = show;
        _h24 = h24;
        _seconds = seconds;
        _showDate = showDate;
        IsVisible = show;
        UpdateTime();
        if (_timer is not null) StartTimer();   // re-arm at the new cadence if already running
    }

    private void StartTimer()
    {
        StopTimer();

        // Tick every second only when seconds are displayed; otherwise the classic ≤1 wake/min budget
        // (R-CL-1) holds. DispatcherTimer already fires on wall-clock intervals — good enough here; we
        // don't chase sub-second boundary alignment.
        var interval = _seconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, OnTimerTick);

        // First tick: update now to catch the initial render.
        UpdateTime();
        _timer.Start();
    }

    /// <summary>The .NET format string for the given 12/24h + seconds settings (pure — unit-tested).</summary>
    internal static string TimeFormat(bool h24, bool seconds)
    {
        var t = h24 ? "HH:mm" : "h:mm";
        if (seconds) t += ":ss";
        if (!h24) t += " tt";
        return t;
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
        var time = now.ToString(TimeFormat(_h24, _seconds));
        TimeDisplay.Text = _showDate ? $"{time}   {now:ddd d MMM}" : time;
        ToolTip.SetTip(TimeDisplay, now.ToLongDateString());
    }
}