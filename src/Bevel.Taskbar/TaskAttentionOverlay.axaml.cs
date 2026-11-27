using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Bevel.Taskbar;

/// <summary>
/// The attention overlay drawn over a taskbar button while its window asks for attention
/// (bevel-qb19). A pure renderer of <see cref="Mode"/>: the VM owns the state machine
/// (Active/Settled/Idle, bounded vs until-activated) and just pushes the resolved visual here.
///
/// Animation is compositor-cheap by construction: the pulse breathes <see cref="Glow"/>'s Opacity,
/// the sweep translates <see cref="SweepStrip"/> horizontally — no layout, no raster, AA untouched.
/// The cycle length is <see cref="TaskAttentionPolicy.PulsePeriod"/> (~1.2s).
/// </summary>
public partial class TaskAttentionOverlay : UserControl
{
    public static readonly StyledProperty<TaskAttentionVisual> ModeProperty =
        AvaloniaProperty.Register<TaskAttentionOverlay, TaskAttentionVisual>(nameof(Mode));

    /// <summary>The visual to render: None hides the overlay, Tint is a static glow, Pulse breathes
    /// the glow, Sweep runs one whoosh per cycle.</summary>
    public TaskAttentionVisual Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    // Opacity range for the pulse glow. Kept low enough to read as a glow on Win2000 flat grey
    // (Industrial1999 Highlight #0A246A is near-black) and on Luna gloss, high enough to notice.
    private const double PulseMinOpacity = 0.15;
    private const double PulseMaxOpacity = 0.5;
    private const double TintOpacity = 0.35;
    private const double SweepOpacity = 0.55;
    private const double TickMs = 40;   // ~25fps, plenty for a 1.2s breathing cycle

    private TranslateTransform SweepTransform => (TranslateTransform)SweepStrip.RenderTransform!;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    /// <summary>The top-level window this overlay lives in, captured on attach so the animation
    /// timer can pause while the bar is hidden (bevel-qb19 review).</summary>
    private TopLevel? _topLevel;
    private IDisposable? _topLevelVisibility;

    /// <summary>Test seam: when false, no DispatcherTimer is started and the animation only advances
    /// through explicit <see cref="TickAt"/> calls (deterministic frames in headless render tests).</summary>
    internal bool AutoTick { get; set; } = true;

    public TaskAttentionOverlay()
    {
        InitializeComponent();
        ApplyMode();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
            ApplyMode();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // bevel-qb19 review: hiding the taskbar window does NOT detach the visual tree, so without
        // this the 25fps pulse timer would run forever on a hidden bar. Track the top-level's
        // IsVisible and pause/resume with it.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevelVisibility = _topLevel?.GetObservable(Visual.IsVisibleProperty)
            .Subscribe(_ => SyncTimerToTopLevelVisibility());
        ApplyMode();   // re-arm in case the mode changed while detached
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevelVisibility?.Dispose();
        _topLevelVisibility = null;
        _topLevel = null;
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // The sweep band is a fixed fraction of the button width, re-derived on resize (still no
        // layout thrash — this only reads Bounds and sets the strip's width once per size change).
        SweepStrip.Width = Math.Max(24, e.NewSize.Width * 0.4);
        if (Mode == TaskAttentionVisual.Sweep)
            TickAt(_clock.Elapsed.TotalMilliseconds);
    }

    private void ApplyMode()
    {
        var mode = Mode;
        switch (mode)
        {
            case TaskAttentionVisual.None:
                StopTimer();
                IsVisible = false;
                break;
            case TaskAttentionVisual.Tint:
                StopTimer();
                IsVisible = true;
                Glow.Opacity = TintOpacity;
                SweepStrip.Opacity = 0;
                break;
            case TaskAttentionVisual.Pulse:
            case TaskAttentionVisual.Sweep:
                IsVisible = true;
                _clock.Restart();
                SyncTimerToTopLevelVisibility();
                TickAt(0);
                break;
        }
    }

    /// <summary>bevel-qb19 review: pauses the animation timer while the top-level window is hidden
    /// (hiding does not detach the visual tree, so the timer would otherwise tick forever on a
    /// hidden/occluded bar) and resumes it when the bar is visible again.</summary>
    private void SyncTimerToTopLevelVisibility()
    {
        if (Mode is not (TaskAttentionVisual.Pulse or TaskAttentionVisual.Sweep)) return;
        if (_topLevel?.IsVisible == false) StopTimer();
        else StartTimer();
    }

    private void StartTimer()
    {
        if (_timer is not null || !AutoTick) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => TickAt(_clock.Elapsed.TotalMilliseconds);
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Advances the animation to <paramref name="elapsedMs"/> into the current cycle loop.
    /// Internal so render tests can drive deterministic frames without a running dispatcher.</summary>
    internal void TickAt(double elapsedMs)
    {
        var period = TaskAttentionPolicy.PulsePeriod.TotalMilliseconds;
        var phase = (elapsedMs % period) / period;   // 0..1 within the cycle
        switch (Mode)
        {
            case TaskAttentionVisual.Pulse:
                // Breathing: cosine ease so it slows at both extremes — reads as a glow, not a blink.
                Glow.Opacity = PulseMinOpacity + (PulseMaxOpacity - PulseMinOpacity) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
                SweepStrip.Opacity = 0;
                break;
            case TaskAttentionVisual.Sweep:
                Glow.Opacity = 0;
                var width = Bounds.Width;
                if (width <= 0) width = 100;   // not laid out yet (tests) — any positive span works
                var strip = SweepStrip.Width;
                SweepTransform.X = -strip + phase * (width + strip);   // one whoosh L→R per cycle
                SweepStrip.Opacity = SweepOpacity;
                break;
            case TaskAttentionVisual.Tint:
                Glow.Opacity = TintOpacity;
                SweepStrip.Opacity = 0;
                break;
        }
    }

    // ── Test seams (read-only views of the animated values) ─────────────
    internal double GlowOpacityForTest => Glow.Opacity;
    internal double SweepXForTest => SweepTransform.X;
    internal double SweepOpacityForTest => SweepStrip.Opacity;
    /// <summary>Test seam: true while the animation DispatcherTimer is running (bevel-qb19 review:
    /// it must be stopped while the top-level window is hidden).</summary>
    internal bool TimerRunningForTest => _timer is not null;
}
