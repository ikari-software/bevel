using System.Diagnostics;
using Bevel.Pal.MacOS;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Tests for HelperLifecycle's monitor loop (bevel-mox): exactly one loop for the
/// lifetime, one relaunch per crash, and a start that degrades gracefully instead of
/// aborting when the helper cannot launch (bevel-c0y prerequisite).
/// </summary>
public class HelperLifecycleTests
{
    /// <summary>
    /// A HelperLifecycle whose process launch/liveness is simulated in-memory, so the
    /// monitor logic can be exercised without a real BevelHelper binary.
    /// </summary>
    private sealed class FakeHelperLifecycle : HelperLifecycle
    {
        private volatile bool _alive;
        private int _concurrentLaunches;

        public FakeHelperLifecycle() : base(NullLogger<HelperLifecycle>.Instance) { }

        public int LaunchCount;
        public int MaxConcurrentLaunches;
        public bool FailLaunch;

        protected override bool IsHelperAlive => _alive;
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(20);

        protected override async Task LaunchAndConnectAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _concurrentLaunches);
            MaxConcurrentLaunches = Math.Max(MaxConcurrentLaunches, n);
            Interlocked.Increment(ref LaunchCount);
            try
            {
                // Hold the "launch" briefly so overlapping monitor loops (the bug) would
                // be observed as concurrent launches.
                await Task.Delay(25, ct);
                if (FailLaunch)
                    throw new InvalidOperationException("simulated launch failure");
                _alive = true;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentLaunches);
            }
        }

        public void SimulateCrash() => _alive = false;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        if (!condition())
            throw new TimeoutException("condition was not met in time");
    }

    [Fact]
    public async Task Each_crash_triggers_exactly_one_relaunch_from_a_single_loop()
    {
        var h = new FakeHelperLifecycle();
        await h.StartAsync(CancellationToken.None);

        await WaitUntil(() => h.LaunchCount >= 1);
        await WaitUntil(() => h.IsRunning);

        for (var expected = 2; expected <= 4; expected++)
        {
            h.SimulateCrash();
            await WaitUntil(() => h.LaunchCount >= expected);
            await WaitUntil(() => h.IsRunning);

            // Let any *duplicate* loops (the bevel-mox bug) over-relaunch if they exist.
            await Task.Delay(120);
            Assert.Equal(expected, h.LaunchCount);
        }

        // The decisive invariant: never more than one launch in flight at once, i.e. a
        // single monitor loop. Duplicate loops would overlap here.
        Assert.Equal(1, h.MaxConcurrentLaunches);

        await h.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_does_not_throw_when_launch_fails_and_keeps_retrying()
    {
        var h = new FakeHelperLifecycle { FailLaunch = true };

        // StartAsync must return promptly without throwing even though the helper fails.
        var ex = await Record.ExceptionAsync(() => h.StartAsync(CancellationToken.None));
        Assert.Null(ex);

        // The monitor keeps retrying in the background.
        await WaitUntil(() => h.LaunchCount >= 3);
        Assert.False(h.IsRunning);
        Assert.Equal(1, h.MaxConcurrentLaunches);

        await h.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_ends_the_monitor_loop()
    {
        var h = new FakeHelperLifecycle();
        await h.StartAsync(CancellationToken.None);
        await WaitUntil(() => h.IsRunning);

        await h.StopAsync(CancellationToken.None);

        var countAfterStop = h.LaunchCount;
        h.SimulateCrash();
        await Task.Delay(150);

        // No further relaunches after Stop.
        Assert.Equal(countAfterStop, h.LaunchCount);
    }

    [Fact]
    public void HardenSocketDirectory_creates_an_owner_only_directory()
    {
        // bevel-164: the socket dir must be 0700 so no other user can reach the UDS.
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-sock-{Guid.NewGuid():N}");
        try
        {
            HelperLifecycle.HardenSocketDirectory(dir);

            Assert.True(Directory.Exists(dir));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(dir));
            }

            // Idempotent: re-hardening an existing dir keeps the restrictive mode.
            HelperLifecycle.HardenSocketDirectory(dir);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(dir); } catch { /* best effort */ }
        }
    }
}
