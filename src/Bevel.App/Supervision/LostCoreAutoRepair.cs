using Bevel.Pal.Abstractions;

namespace Bevel.App.Supervision;

/// <summary>
/// Peer-side complement to the launcher's core-socket health probe (bevel-hprv). The supervisor
/// only sees "process alive + core.sock exists"; a core that is bound but not serving still
/// leaves the taskbar reconnecting forever. After a sustained disconnect this asks the launcher
/// to respawn just the core — the same verb as the pulsing-indicator repair, without waiting
/// for a click.
///
/// Capped: a crash-looping core must not be restarted from the UI side on every backoff tick.
/// The launcher's own monitor owns unbounded retry-with-backoff.
/// </summary>
internal sealed class LostCoreAutoRepair : IDisposable
{
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(8);
    internal const int DefaultMaxAttempts = 3;

    private readonly IShellConnectionStatus _status;
    private readonly Func<bool> _restartCore;
    private readonly TimeSpan _grace;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _arm;
    private int _attempts;
    private bool _disposed;

    public LostCoreAutoRepair(
        IShellConnectionStatus status,
        Func<bool> restartCore,
        TimeSpan? grace = null,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
    {
        _status = status;
        _restartCore = restartCore;
        _grace = grace ?? DefaultGrace;
        _maxAttempts = Math.Max(1, maxAttempts);
        _delay = delay ?? Task.Delay;
        _log = log;
        _status.ConnectionChanged += OnConnectionChanged;
        if (!_status.IsConnected) Arm();
    }

    /// <summary>How many RestartCore attempts have been issued this process lifetime.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (_disposed) return;
        if (connected) Disarm();
        else Arm();
    }

    private void Arm()
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (_disposed || _attempts >= _maxAttempts) return;
            _arm?.Cancel();
            _arm?.Dispose();
            _arm = new CancellationTokenSource();
            ct = _arm.Token;
        }
        _ = WaitAndRepairAsync(ct);
    }

    private void Disarm()
    {
        lock (_gate)
        {
            _arm?.Cancel();
            _arm?.Dispose();
            _arm = null;
        }
    }

    private async Task WaitAndRepairAsync(CancellationToken ct)
    {
        try
        {
            await _delay(_grace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested || _disposed || _status.IsConnected) return;
        var n = Interlocked.Increment(ref _attempts);
        if (n > _maxAttempts)
        {
            Interlocked.Decrement(ref _attempts);
            return;
        }
        _log?.Invoke($"lost-core: still disconnected after {_grace.TotalSeconds:0}s — RestartCore (attempt {n}/{_maxAttempts})");
        try { _restartCore(); }
        catch (Exception ex) { _log?.Invoke($"lost-core: RestartCore threw ({ex.Message})"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _status.ConnectionChanged -= OnConnectionChanged;
        Disarm();
    }
}
