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
        await Task.Delay(80);
        Assert.Equal(1, restarts);
        Assert.Equal(1, repair.Attempts);
    }

    [Fact]
    public async Task Cancels_the_pending_repair_when_the_link_returns()
    {
        var status = new FakeStatus();
        var restarts = 0;
        using var repair = new LostCoreAutoRepair(
            status, () => { Interlocked.Increment(ref restarts); return true; },
            grace: TimeSpan.FromMilliseconds(200),
            delay: Task.Delay);

        status.Set(false);
        await Task.Delay(30);
        status.Set(true);
        await Task.Delay(250);
        Assert.Equal(0, restarts);
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

        // Still disconnected after each attempt — should stop at the cap, not spin.
        await Task.Delay(80);
        status.Set(false); // re-arm; must not exceed the cap
        await Task.Delay(50);
        Assert.True(restarts <= 2, $"expected at most 2 auto-restarts, got {restarts}");
        Assert.True(repair.Attempts <= 2);
    }
}
