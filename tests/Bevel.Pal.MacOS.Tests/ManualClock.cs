using Bevel.Pal.MacOS;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Manual IClock (bevel-umgr — moved out of HelperLifecycleTests so the stream-loop tests share
/// it): every Delay parks on a completion source until the test calls <see cref="Tick"/>.
/// <see cref="NextDelayArrived"/> signals once per Delay call, so a test can await "the loop is
/// parked again" instead of sleeping. Requested delays are recorded so backoff schedules are
/// assertable.
/// </summary>
internal sealed class ManualClock : IClock
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _waiters = new();
    private readonly SemaphoreSlim _arrivals = new(0);
    private readonly List<TimeSpan> _requested = new();

    public IReadOnlyList<TimeSpan> Requested { get { lock (_gate) return _requested.ToList(); } }

    public int PendingCount
    {
        get { lock (_gate) return _waiters.Count(w => !w.Task.IsCompleted); }
    }

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_gate)
        {
            _requested.Add(delay);
            _waiters.Add(tcs);
        }
        _arrivals.Release();
        return tcs.Task;
    }

    /// <summary>
    /// Awaits the next Delay call parking (one signal per Delay call). The 5s bound is a
    /// failure backstop so a broken loop fails the test instead of hanging it — it never
    /// gates a passing run's behavior.
    /// </summary>
    public async Task NextDelayArrived()
    {
        if (!await _arrivals.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The monitor loop never parked on the clock.");
    }

    /// <summary>Releases the oldest still-pending waiter — exactly one loop iteration proceeds.</summary>
    public void Tick()
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            var idx = _waiters.FindIndex(w => !w.Task.IsCompleted);
            if (idx < 0) throw new InvalidOperationException("No pending delay to release.");
            tcs = _waiters[idx];
            _waiters.RemoveAt(idx);
        }
        tcs.TrySetResult();
    }
}
