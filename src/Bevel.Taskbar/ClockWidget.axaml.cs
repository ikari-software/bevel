using System;
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

    /// <summary>
    /// Where the clock reads the time. Defaults to the system clock; the landing-page render pins it so
    /// a harvested screenshot does not change every minute — a live clock makes the shot's bytes differ
    /// on every run, which would fail the site's drift check on every run and train everyone to ignore it.
    /// </summary>
    public TimeProvider Time
    {
        get => _time;
        set { _time = value; UpdateTime(); }   // repaint on assignment, or the display keeps the old source's reading
    }

    private TimeProvider _time = TimeProvider.System;

    private int _rows = 1;

    /// <summary>Track the taskbar row count. On a multi-row bar the date moves under the time; on a single
    /// row it stays inline. Called from the taskbar's ApplyRowLayout.</summary>
    public void SetRows(int rows)
    {
        rows = Math.Max(1, rows);
        if (_rows == rows) return;
        _rows = rows;
        UpdateTime();
    }

    private void UpdateTime()
    {
        var now = Time.GetLocalNow().DateTime;
        var time = now.ToString(TimeFormat(_h24, _seconds));
        var date = now.ToString("ddd d MMM");
        var stacked = _showDate && _rows > 1;                 // second line only when there's vertical room
        TimeDisplay.Text = _showDate && !stacked ? $"{time}   {date}" : time;
        DateDisplay.Text = date;
        DateDisplay.IsVisible = stacked;
        ToolTip.SetTip(TimeDisplay, now.ToLongDateString());
    }
}