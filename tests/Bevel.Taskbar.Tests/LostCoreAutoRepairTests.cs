using System.Diagnostics;
using Bevel.App.Supervision;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

public class LostCoreAutoRepairTests
{
    private sealed class FakeStatus : IShellConnectionStatus
    {
        public bool IsConnected { get; set; } = true;
        public event EventHandler<bool>? ConnectionChanged;
        public void Set(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, connected);
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds, and fails with <paramref name="because"/>
    /// if it never does.
    ///
    /// <para>The repair fires from a timer continuation on the thread pool, so the ONLY safe wait is on
    /// the outcome. A fixed sleep sized to the grace period is a race the CI runner wins whenever it is
    /// loaded: at <c>grace: 20ms</c> + <c>Task.Delay(80)</c> this file passed on Linux, Windows and a
    /// dev Mac while failing on macos-latest. Waiting on the condition makes a slow machine slow rather
    /// than red, and the timeout is deliberately orders of magnitude above the grace so a genuine
    /// regression still reports as a failure and not as a hang.</para></summary>
    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"timed out after {timeoutMs}ms waiting for {because}");
    }

    [Fact]
    public async Task Requests_RestartCore_after_the_grace_period()
    {
        var status = new FakeStatus();
        var restarts = 0;
        using var repair = new LostCoreAutoRepair(
            status, () => { Interlocked.Increment(ref restarts); return true; },
            grace: TimeSpan.FromMilliseconds(20),
            delay: Task.Delay);

        status.Set(false);

        await WaitUntil(() => Volatile.Read(ref restarts) == 1, "the sustained disconnect to request one RestartCore");
        Assert.Equal(1, repair.Attempts);
    }

    [Fact]
    public async Task Cancels_the_pending_repair_when_the_link_returns()
    {
        var status = new FakeStatus();
        var restarts = 0;
        // The grace is long on purpose: the point under test is that reconnecting INSIDE it disarms the
        // repair, so the reconnect has to land comfortably inside the window even on a stalled runner.
        var grace = TimeSpan.FromMilliseconds(500);
        using var repair = new LostCoreAutoRepair(
            status, () => { Interlocked.Increment(ref restarts); return true; },
            grace: grace,
            delay: Task.Delay);

        status.Set(false);
        status.Set(true);

        // Negative assertion, so there is no condition to wait on — wait out several full grace periods
        // and require that the disarmed repair never fired.
        await Task.Delay(grace * 3);
        Assert.Equal(0, Volatile.Read(ref restarts));
        Assert.Equal(0, repair.Attempts);
    }

    [Fact]
    public async Task Caps_auto_restarts()
    {
        var status = new FakeStatus { IsConnected = false };
        var restarts = 0;
        using var repair = new LostCoreAutoRepair(
            status, () => { Interlocked.Increment(ref restarts); return true; },
            grace: TimeSpan.FromMilliseconds(10),
            maxAttempts: 2,
            delay: Task.Delay);

        // The cap counts attempts over the process lifetime, across SEPARATE disconnects — it is not a
        // retry loop. Arm() runs on a disconnect EDGE, so one sustained outage yields exactly one
        // repair however long it lasts; reaching the cap means flapping the link. Asserting "<= 2"
        // against a single outage therefore passed at one attempt, which is also what it would read if
        // the cap were broken and firing on every tick.
        await WaitUntil(() => Volatile.Read(ref restarts) == 1, "the first outage to request one RestartCore");

        status.Set(true);
        status.Set(false);
        await WaitUntil(() => Volatile.Read(ref restarts) == 2, "the second outage to request a second RestartCore");

        // Third outage: over the cap, so it must be refused rather than served.
        status.Set(true);
        status.Set(false);
        await Task.Delay(100);

        Assert.Equal(2, Volatile.Read(ref restarts));
        Assert.Equal(2, repair.Attempts);
    }
}
