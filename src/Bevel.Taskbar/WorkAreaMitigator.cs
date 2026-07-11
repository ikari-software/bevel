using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Work-area overlap mitigation engine (U12, Nudge strategy). Detects windows whose
/// bottom edge crosses into the taskbar band and shrinks them to sit above it, via
/// IWindowManager.RepositionAsync — rate-limited per window so an app that re-asserts
/// its own frame can't cause a reposition fight (02 Req 9.2), and suspended while a
/// window is in motion so a drag/resize isn't fought mid-gesture (02 Req 9.x).
/// </summary>
public sealed class WorkAreaMitigator : IDisposable
{
    /// <summary>Never shrink a window below this height — a squashed window is worse than an overlap.</summary>
    private const int MinUsableHeight = 100;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IWindowManager _windowManager;
    private readonly SettingsService _settings;
    private readonly Func<PalRect?> _taskbarBand;
    private readonly TimeProvider _time;
    private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, DateTime> _lastReposition = new();
    private readonly Dictionary<string, PalRect> _lastBounds = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
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

    /// <summary>Starts the overlap detection loop. Idempotent.</summary>
    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
                // Gate every tick: the user can switch strategy at runtime, and a PAL that
                // can't reposition (feature detection) must never be asked to.
                if (_settings.Current.WorkAreaStrategy != WorkAreaStrategy.Nudge) continue;
                if (!_windowManager.Capabilities.SupportsReposition) continue;
                await MitigateOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* best effort — next tick retries */ }
        }
    }

    /// <summary>
    /// One mitigation pass. Nudges every stationary, non-minimized window that overlaps the
    /// taskbar band and isn't rate-limited. Returns the number of windows repositioned.
    /// Internal so the decision logic can be driven a tick at a time under test, free of the
    /// poll loop's timing.
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

            // Drag/resize suspension: if the frame changed since the last tick the window is
            // in motion (or just appeared) — record it and leave it alone this pass. A window
            // that has come to rest is nudged on the following tick. This also means we never
            // re-nudge a window we just moved (its frame changed), which the rate-limit backs up.
            var moved = !_lastBounds.TryGetValue(id, out var prev) || prev != w.Bounds;
            _lastBounds[id] = w.Bounds;
            if (moved) continue;

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

    /// <summary>Drops per-window bookkeeping for windows that are no longer present.</summary>
    private void Prune(HashSet<string> live)
    {
        if (_lastBounds.Count > live.Count)
            foreach (var stale in _lastBounds.Keys.Where(k => !live.Contains(k)).ToList())
                _lastBounds.Remove(stale);
        if (_lastReposition.Count > live.Count)
            foreach (var stale in _lastReposition.Keys.Where(k => !live.Contains(k)).ToList())
                _lastReposition.Remove(stale);
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
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _loopTask = null;
    }
}
