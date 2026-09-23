using System.Collections.Concurrent;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Work-area overlap mitigation engine (U12, Nudge strategy). Shrinks windows whose bottom
/// edge crosses into the taskbar band so they sit above it, via IWindowManager.RepositionAsync.
///
/// <para><b>This is a mitigation, not a reservation — by platform necessity.</b> macOS has no public
/// API to reserve screen work area (no <c>_NET_WM_STRUT</c> equivalent): <c>NSScreen.visibleFrame</c>
/// is computed by the system from the menu bar and the <i>Dock</i> only, never from an arbitrary
/// utility window like our bar. So every OS-driven "fill the screen" gesture — the green button /
/// <c>zoom:</c>, Window ▸ Zoom, double-clicking the title bar, AppKit's own
/// <c>constrainFrameRect:toScreen:</c> — targets a rect that INCLUDES our band, and always will.
/// The two ways out are both rejected for shipping builds: reserving via a visible Dock at the
/// measured Dock inset is the opt-in "Dock shim" strict mode (spec 02 Req 9.3), which is
/// measured-and-deferred (docs/spike/2026-07-12-dock-inset-measurement.md — the inset was not
/// measurable on reference hardware and it forces a visible Dock against the user's preference);
/// and private SkyLight work-area symbols are ruled out outright (spec 02 Req 9.5). Hence: we let
/// the zoom happen and correct the window afterwards, which is what Req 9.2 mandates and
/// documents as "best-effort and jittery".</para>
///
/// <para>Event-driven for snappiness: it reacts to window open/move/resize/focus events and mitigates
/// a short <see cref="SettleDelay"/> after activity stops — so a maximize/zoom is corrected in
/// a few hundred ms, not on a slow poll tick. A live drag keeps re-arming that debounce, so the
/// bar is never fought mid-gesture; we act once, after the window comes to rest. A slow safety
/// poll re-checks in case an app doesn't emit AX move/resize events.</para>
///
/// <para>Two properties exist specifically because a <i>zoom</i> is an animation into a rect that
/// covers the bar (bevel-yv2m):</para>
/// <list type="bullet">
///   <item><b>A verify pass</b> (<see cref="VerifyDelay"/>, chained at most
///   <see cref="MaxVerifyPasses"/> deep). AppKit animates <c>zoom:</c> into <c>visibleFrame</c>, so
///   the frame we corrected can be superseded by the animation's own final frame a beat later. One
///   cheap re-check after each correction catches that instead of leaving the window sitting over
///   the bar until the next safety tick.</item>
///   <item><b>Progress-aware back-off</b> instead of a blind per-window cooldown. Req 9.2 asks for
///   rate limiting so an app that re-asserts its own frame can't cause a reposition fight — that
///   is a fight only when the window comes back at the SAME frame we just corrected. A window that
///   turns up over the band at a <i>new</i> frame (the user zoomed again, dragged it down again)
///   has genuinely moved, and is corrected at once rather than being locked out for the cooldown.
///   A blind cooldown made a re-zoom right after a correction sit over the bar for seconds.</item>
/// </list>
/// </summary>
public sealed class WorkAreaMitigator : IDisposable
{
    /// <summary>Never shrink a window below this height — a squashed window is worse than an overlap.</summary>
    private const int MinUsableHeight = 100;

    /// <summary>How long window activity must be quiet before we mitigate. A live drag/resize
    /// emits a stream of events that keep re-arming this, so it only fires once motion stops —
    /// kept short so the correction feels immediate. If an app emits events too sparsely and
    /// this fires mid-animation, the animation's final event simply re-arms and corrects it.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Backstop for apps that don't emit AX move/resize events: re-check periodically.</summary>
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long after a correction we re-check the windows we touched. Sized to outlast a
    /// stock AppKit zoom animation (~0.2 s), which can land its final frame — the full
    /// <c>visibleFrame</c>, band included — after our correction (bevel-yv2m). Superseded by any real
    /// window activity in the meantime, which arms an ordinary settle pass instead.</summary>
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How many verify passes may chain off one another before we fall back to the safety
    /// poll. Bounds the loop with an app that keeps re-asserting new frames; an animation converges
    /// well inside this.</summary>
    private const int MaxVerifyPasses = 3;

    private readonly IWindowManager _windowManager;
    private readonly ISettingsService _settings;
    private readonly Func<PalRect?> _taskbarBand;
    private readonly TimeProvider _time;
    private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(2);
    // The last correction we attempted per window: the offending frame we saw (Source) and when.
    // Keyed by window id. Thread-safe: a settle pass already inside MitigateOnceAsync (awaiting slow
    // RepositionAsync IPC) is not reliably cancelled before a newly-armed pass starts, so two passes
    // can touch this concurrently on thread-pool threads. ConcurrentDictionary keeps that from
    // tearing the map.
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new();

    /// <summary>One attempted correction: the overlapping frame we acted on, and when.</summary>
    private readonly record struct Attempt(PalRect Source, DateTime At);
    private readonly object _gate = new();
    private CancellationTokenSource? _settleCts;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _started;
    private bool _disposed;

    /// <param name="taskbarBand">Supplies the taskbar's current screen rect in the window
    /// manager's coordinate space (top-left-origin global points), or null when unknown —
    /// mitigation is skipped until real geometry is available. A delegate, not a snapshot,
    /// so display reconfigurations are picked up live.</param>
    /// <param name="timeProvider">Clock for rate-limiting; defaults to the system clock.
    /// Injectable so tests can advance time deterministically.</param>
    public WorkAreaMitigator(IWindowManager windowManager, ISettingsService settings,
        Func<PalRect?> taskbarBand, TimeProvider? timeProvider = null)
    {
        _windowManager = windowManager;
        _settings = settings;
        _taskbarBand = taskbarBand;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Subscribes to window activity and starts the safety poll. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            // React the instant a window opens, moves, resizes, or focus changes — those are the
            // moments a window can slide under the taskbar. Bursts (a live drag) coalesce via the
            // settle debounce so we act once, after motion stops.
            _windowManager.WindowOpened += OnActivity;
            _windowManager.WindowChanged += OnActivity;
            _windowManager.ForegroundChanged += OnActivity;
            _pollCts = new CancellationTokenSource();
            _pollTask = SafetyPollAsync(_pollCts.Token);
        }
    }

    private void OnActivity(object? sender, ForeignWindow w) => ArmSettle();

    /// <summary>
    /// Kicks an immediate (debounced) mitigation pass for a trigger outside the window event
    /// stream — chiefly a display reconfiguration, which moves the taskbar band without moving any
    /// window, so no WindowChanged/Opened event would otherwise fire. Routes through the same
    /// settle debounce, so it can't fight a live drag and coalesces with concurrent activity.
    /// </summary>
    public void RequestMitigation() => ArmSettle();

    /// <summary>(Re)arms the settle debounce: cancels any pending pass and schedules a fresh one
    /// <see cref="SettleDelay"/> from now. Repeated activity keeps pushing it out until the
    /// window comes to rest, which is the drag/resize suspension — we never nudge mid-gesture.</summary>
    /// <param name="delay">How long to wait before the pass. <see cref="SettleDelay"/> for real
    /// activity; <see cref="VerifyDelay"/> for a post-correction re-check.</param>
    /// <param name="verifyDepth">How many verify passes already chained into this one; 0 for a pass
    /// armed by real activity.</param>
    private void ArmSettle(TimeSpan? delay = null, int verifyDepth = 0)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            var prev = _settleCts;
            _settleCts = cts = new CancellationTokenSource();
            prev?.Cancel();
            prev?.Dispose();   // the superseded source was never disposed — one leaked per re-arm (ce-review)
        }
        _ = SettleAndMitigateAsync(delay ?? SettleDelay, verifyDepth, cts.Token);
    }

    private async Task SettleAndMitigateAsync(TimeSpan delay, int verifyDepth, CancellationToken ct)
    {
        try { await Task.Delay(delay, _time, ct); }
        catch (OperationCanceledException) { return; } // superseded by newer activity, or disposed
        // Gate here (not at wire-up): the user can switch strategy at runtime, and a PAL that
        // can't reposition must never be asked to.
        if (_settings.Current.WorkAreaStrategy != WorkAreaStrategy.Nudge) return;
        if (!_windowManager.Capabilities.SupportsReposition) return;
        try
        {
            var corrected = await MitigateOnceAsync(ct);
            // A zoom animates into NSScreen.visibleFrame — our band included — so the frame we just
            // corrected can be overwritten by the animation's own final frame moments later, and the
            // app may emit no further AX event for it. Re-check once after each correction so that
            // lands in ~half a second instead of waiting on the safety poll (bevel-yv2m). Skipped
            // when newer activity already superseded us: that pass covers the same ground.
            if (corrected > 0 && verifyDepth < MaxVerifyPasses && !ct.IsCancellationRequested)
                ArmSettle(VerifyDelay, verifyDepth + 1);
        }
        catch (OperationCanceledException) { }
        catch { /* best effort — the next event or safety tick retries */ }
    }

    private async Task SafetyPollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(SafetyPollInterval, _time, ct); }
            catch (OperationCanceledException) { break; }
            ArmSettle(); // route through the same debounce so the poll never fights a live drag
        }
    }

    /// <summary>
    /// One mitigation pass. Nudges every non-minimized window that overlaps the taskbar band
    /// and isn't rate-limited. Returns the number of windows repositioned. Internal so the
    /// decision logic can be driven directly under test, free of the event/debounce timing
    /// (motion suspension lives in <see cref="ArmSettle"/>, verified live).
    /// </summary>
    internal async Task<int> MitigateOnceAsync(CancellationToken ct = default)
    {
        if (_taskbarBand() is not { } band) return 0;

        var windows = await _windowManager.EnumerateAsync(ct);
        var live = new HashSet<string>(windows.Count);
        var repositioned = 0;

        foreach (var w in windows)
        {
            var id = w.Id.Value;
            live.Add(id);

            if (w.IsMinimized || !Overlaps(w.Bounds, band))
            {
                // Clear of the band (or gone from view): whatever we last attempted for this window
                // took, so it can be corrected again immediately next time it crosses in — a
                // re-zoom must not inherit the previous correction's back-off.
                _attempts.TryRemove(id, out _);
                continue;
            }

            if (IsRepositionFight(id, w.Bounds)) continue;

            // Shrink the window so its bottom edge sits on the band's top edge.
            var newHeight = band.Y - w.Bounds.Y;
            if (newHeight < MinUsableHeight) continue; // never squash a window into unusability

            await _windowManager.RepositionAsync(w.Id, w.Bounds with { Height = newHeight }, ct);
            _attempts[id] = new Attempt(w.Bounds, _time.GetUtcNow().UtcDateTime);
            repositioned++;
        }

        Prune(live);
        return repositioned;
    }

    /// <summary>Drops attempt bookkeeping for windows that are no longer present.</summary>
    private void Prune(HashSet<string> live)
    {
        if (_attempts.Count > live.Count)
            foreach (var stale in _attempts.Keys.Where(k => !live.Contains(k)).ToList())
                _attempts.TryRemove(stale, out _);
    }

    private static bool Overlaps(PalRect w, PalRect band)
        => w.Y < band.Y + band.Height
        && w.Y + w.Height > band.Y
        && w.X < band.X + band.Width
        && w.X + w.Width > band.X;

    /// <summary>
    /// Rate limiting per 02 Req 9.2, scoped to what it is actually for: an app that re-asserts its
    /// own frame against us. True (back off) only when the window has come back over the band at the
    /// EXACT frame we already tried to correct, within <see cref="_rateLimit"/> — that is a
    /// reposition fight, and one attempt per limiter window is all it gets. A different frame means
    /// the window genuinely moved since (a fresh zoom, another drag down) and is corrected at once.
    /// </summary>
    private bool IsRepositionFight(string windowId, PalRect bounds)
        => _attempts.TryGetValue(windowId, out var last)
        && last.Source == bounds
        && _time.GetUtcNow().UtcDateTime - last.At <= _rateLimit;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started)
            {
                _windowManager.WindowOpened -= OnActivity;
                _windowManager.WindowChanged -= OnActivity;
                _windowManager.ForegroundChanged -= OnActivity;
            }
            _settleCts?.Cancel();
            _settleCts?.Dispose();
            _pollCts?.Cancel();
            _pollCts?.Dispose();
        }
    }
}
