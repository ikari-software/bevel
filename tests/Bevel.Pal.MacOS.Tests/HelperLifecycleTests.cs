using Bevel.Ipc;
using Bevel.Pal.MacOS;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Deterministic tests for HelperLifecycle's monitor loop (bevel-mox / bevel-vgg): the loop is
/// driven by a <see cref="ManualClock"/> whose Delay parks until the test releases a tick, so
/// every iteration happens exactly when the test allows it — relaunch-per-crash, launch-failure
/// backoff, and stop behavior are asserted structurally, with no real-time sleeps or races.
/// </summary>
public class HelperLifecycleTests
{
    // ── deterministic collaborators ─────────────────────────────────────────

    /// <summary>In-memory IHelperProcessHost: launch/liveness simulated, launches counted.</summary>
    private sealed class FakeHost : IHelperProcessHost
    {
        private volatile bool _alive;

        public int LaunchCount;
        public bool FailLaunch;

        public string NonceToken => "test-nonce";
        public string SocketPath => "";
        public HelperClient? Client => null;
        public bool IsAlive => _alive;

        public Task LaunchAndConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref LaunchCount);
            if (FailLaunch)
                throw new InvalidOperationException("simulated launch failure");
            _alive = true;
            return Task.CompletedTask;
        }

        public void Kill() => _alive = false;
        public void SimulateCrash() => _alive = false;
        public void Dispose() { }
    }

    /// <summary>
    /// Manual IClock: every Delay parks on a completion source until the test calls
    /// <see cref="Tick"/>. <see cref="NextDelayArrived"/> signals once per Delay call, so a
    /// test can await "the loop is parked again" instead of sleeping. Requested delays are
    /// recorded so backoff schedules are assertable.
    /// </summary>
    private sealed class ManualClock : IClock
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

    private static (HelperLifecycle Lifecycle, FakeHost Host, ManualClock Clock) Build(bool failLaunch = false)
    {
        var host = new FakeHost { FailLaunch = failLaunch };
        var clock = new ManualClock();
        var lifecycle = new HelperLifecycle(
            NullLogger<HelperLifecycle>.Instance, host, clock, pollInterval: TimeSpan.FromSeconds(1));
        return (lifecycle, host, clock);
    }

    // ── relaunch policy ─────────────────────────────────────────────────────

    [Fact]
    public async Task Each_crash_triggers_exactly_one_relaunch_from_a_single_loop()
    {
        var (h, host, clock) = Build();
        await h.StartAsync(CancellationToken.None);

        // First iteration launches, then parks on the poll delay.
        await clock.NextDelayArrived();
        Assert.Equal(1, host.LaunchCount);
        Assert.True(h.IsRunning);

        for (var expected = 2; expected <= 4; expected++)
        {
            host.SimulateCrash();
            clock.Tick();                    // allow exactly one loop iteration
            await clock.NextDelayArrived();  // it relaunched and parked again

            Assert.Equal(expected, host.LaunchCount); // exactly one relaunch per crash
            Assert.True(h.IsRunning);
            Assert.Equal(1, clock.PendingCount);      // exactly one monitor loop parked
        }

        await h.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_does_not_throw_when_launch_fails_and_retries_with_doubling_backoff()
    {
        var (h, host, clock) = Build(failLaunch: true);

        // StartAsync must return promptly without throwing even though the helper fails.
        var ex = await Record.ExceptionAsync(() => h.StartAsync(CancellationToken.None));
        Assert.Null(ex);

        // Each failed launch parks on a retry delay; releasing it permits exactly one retry.
        await clock.NextDelayArrived();
        Assert.Equal(1, host.LaunchCount);
        for (var expected = 2; expected <= 4; expected++)
        {
            clock.Tick();
            await clock.NextDelayArrived();
            Assert.Equal(expected, host.LaunchCount);
        }
        Assert.False(h.IsRunning);

        // The retry schedule doubles from the poll interval: 1s, 2s, 4s, 8s…
        Assert.Equal(
            new[] { 1.0, 2.0, 4.0, 8.0 },
            clock.Requested.Take(4).Select(t => t.TotalSeconds));

        await h.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_successful_launch_resets_the_retry_backoff()
    {
        var (h, host, clock) = Build(failLaunch: true);
        await h.StartAsync(CancellationToken.None);

        await clock.NextDelayArrived();      // failure #1 parked (1s)
        clock.Tick();
        await clock.NextDelayArrived();      // failure #2 parked (2s)

        host.FailLaunch = false;             // helper is healthy again
        clock.Tick();
        await clock.NextDelayArrived();      // relaunch succeeded; parked on poll delay

        Assert.True(h.IsRunning);
        Assert.Equal(1.0, clock.Requested[^1].TotalSeconds); // back to the poll interval

        await h.StopAsync(CancellationToken.None);
    }

    // ── stop ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_ends_the_monitor_loop_and_kills_the_helper()
    {
        var (h, host, clock) = Build();
        await h.StartAsync(CancellationToken.None);
        await clock.NextDelayArrived();
        Assert.True(h.IsRunning);

        await h.StopAsync(CancellationToken.None);

        Assert.False(h.IsRunning);              // StopAsync killed the helper
        Assert.Equal(0, clock.PendingCount);    // the parked delay was cancelled — loop exited
        Assert.Equal(1, host.LaunchCount);      // and no further relaunches happened

        host.SimulateCrash();
        Assert.Throws<InvalidOperationException>(clock.Tick); // no loop left to wake
        Assert.Equal(1, host.LaunchCount);
    }

    // ── socket dir hardening (bevel-164) ────────────────────────────────────

    [Fact]
    public void HardenSocketDirectory_creates_an_owner_only_directory()
    {
        // bevel-164: the socket dir must be 0700 so no other user can reach the UDS.
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-sock-{Guid.NewGuid():N}");
        try
        {
            HelperProcessHost.HardenSocketDirectory(dir);

            Assert.True(Directory.Exists(dir));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(dir));
            }

            // Idempotent: re-hardening an existing dir keeps the restrictive mode.
            HelperProcessHost.HardenSocketDirectory(dir);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(dir); } catch { /* best effort */ }
        }
    }
}
