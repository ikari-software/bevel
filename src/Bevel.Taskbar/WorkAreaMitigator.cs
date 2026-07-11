using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Work-area overlap mitigation engine (U12, Nudge strategy). Detects windows whose
/// bottom edge crosses into the taskbar band and shrinks them to sit above it, via
/// IWindowManager.RepositionAsync — rate-limited per window so an app that re-asserts
/// its own frame can't cause a reposition fight (02 Req 9.2).
/// </summary>
public sealed class WorkAreaMitigator : IDisposable
{
    private readonly IWindowManager _windowManager;
    private readonly SettingsService _settings;
    private readonly Func<PalRect?> _taskbarBand;
    private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, DateTime> _lastReposition = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    /// <param name="taskbarBand">Supplies the taskbar's current screen rect (device px),
    /// or null when unknown — mitigation is skipped until real geometry is available.
    /// A delegate, not a snapshot, so display reconfigurations are picked up live.</param>
    public WorkAreaMitigator(IWindowManager windowManager, SettingsService settings,
        Func<PalRect?> taskbarBand)
    {
        _windowManager = windowManager;
        _settings = settings;
        _taskbarBand = taskbarBand;
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
                await Task.Delay(1000, ct);
                if (_settings.Current.WorkAreaStrategy != WorkAreaStrategy.Nudge) continue;
                if (!_windowManager.Capabilities.SupportsReposition) continue;
                await MitigateOverlapsAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* best effort — next tick retries */ }
        }
    }

    private async Task MitigateOverlapsAsync(CancellationToken ct)
    {
        if (_taskbarBand() is not { } band) return;

        var windows = await _windowManager.EnumerateAsync(ct);
        foreach (var w in windows)
        {
            if (w.IsMinimized || !Overlaps(w.Bounds, band) || !RateLimitCheck(w.Id.Value))
                continue;

            // Shrink the window so its bottom edge sits on the band's top edge.
            var newHeight = band.Y - w.Bounds.Y;
            if (newHeight < 100) continue; // never squash a window into unusability

            await _windowManager.RepositionAsync(
                w.Id, w.Bounds with { Height = newHeight }, ct);
            _lastReposition[w.Id.Value] = DateTime.UtcNow;
        }
    }

    private static bool Overlaps(PalRect w, PalRect band)
        => w.Y < band.Y + band.Height
        && w.Y + w.Height > band.Y
        && w.X < band.X + band.Width
        && w.X + w.Width > band.X;

    private bool RateLimitCheck(string windowId)
        => !_lastReposition.TryGetValue(windowId, out var last)
        || DateTime.UtcNow - last > _rateLimit;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}