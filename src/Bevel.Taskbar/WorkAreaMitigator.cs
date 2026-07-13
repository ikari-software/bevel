using System.Collections.Concurrent;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Work-area overlap mitigation engine (U12, Nudge strategy). Shrinks windows whose bottom
/// edge crosses into the taskbar band so they sit above it, via IWindowManager.RepositionAsync.
///
/// Event-driven for snappiness: it reacts to window open/move/resize/focus events and mitigates
/// a short <see cref="SettleDelay"/> after activity stops — so a maximize/zoom is corrected in
/// a few hundred ms, not on a slow poll tick. A live drag keeps re-arming that debounce, so the
/// bar is never fought mid-gesture; we act once, after the window comes to rest. A slow safety
/// poll re-checks in case an app doesn't emit AX move/resize events. Repositions are rate-limited
/// per window (02 Req 9.2) so an app that re-asserts its own frame can't cause a reposition fight.
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

    private readonly IWindowManager _windowManager;
    private readonly SettingsService _settings;
    private readonly Func<PalRect?> _taskbarBand;
    private readonly TimeProvider _time;
    private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(2);
    // Thread-safe: a settle pass already inside MitigateOnceAsync (awaiting slow RepositionAsync
    // IPC) is not reliably cancelled before a newly-armed pass starts, so two passes can touch this
    // concurrently on thread-pool threads. ConcurrentDictionary keeps that from tearing the map.
    private readonly ConcurrentDictionary<string, DateTime> _lastReposition = new();
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
    public WorkAreaMitigator(IWindowManager windowManager, SettingsService settings,
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
    private void ArmSettle()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _settleCts?.Cancel();
            _settleCts = cts = new CancellationTokenSource();
        }
        _ = SettleAndMitigateAsync(cts.Token);
    }

    private async Task SettleAndMitigateAsync(CancellationToken ct)
    {
        try { await Task.Delay(SettleDelay, _time, ct); }
        catch (OperationCanceledException) { return; } // superseded by newer activity, or disposed
        // Gate here (not at wire-up): the user can switch strategy at runtime, and a PAL that
        // can't reposition must never be asked to.
        if (_settings.Current.WorkAreaStrategy != WorkAreaStrategy.Nudge) return;
        if (!_windowManager.Capabilities.SupportsReposition) return;
        try { await MitigateOnceAsync(ct); }
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

            if (w.IsMinimized || !Overlaps(w.Bounds, band) || !RateLimitCheck(id))
                continue;

            // Shrink the window so its bottom edge sits on the band's top edge.
            var newHeight = band.Y - w.Bounds.Y;
            if (newHeight < MinUsableHeight) continue; // never squash a window into unusability

            await _windowManager.RepositionAsync(w.Id, w.Bounds with { Height = newHeight }, ct);
            _lastReposition[id] = _time.GetUtcNow().UtcDateTime;
            repositioned++;
        }

        Prune(live);
        return repositioned;
    }

    /// <summary>Drops rate-limit bookkeeping for windows that are no longer present.</summary>
    private void Prune(HashSet<string> live)
    {
        if (_lastReposition.Count > live.Count)
            foreach (var stale in _lastReposition.Keys.Where(k => !live.Contains(k)).ToList())
                _lastReposition.TryRemove(stale, out _);
    }

    private static bool Overlaps(PalRect w, PalRect band)
        => w.Y < band.Y + band.Height
        && w.Y + w.Height > band.Y
        && w.X < band.X + band.Width
        && w.X + w.Width > band.X;

    private bool RateLimitCheck(string windowId)
        => !_lastReposition.TryGetValue(windowId, out var last)
        || _time.GetUtcNow().UtcDateTime - last > _rateLimit;

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
