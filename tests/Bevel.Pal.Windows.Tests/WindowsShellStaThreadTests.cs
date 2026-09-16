using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>Serialize against the live file-stack tests: they share the STA worker and CallTimeout.</summary>
[CollectionDefinition("WindowsShellSta", DisableParallelization = true)]
public class WindowsShellStaCollection;

[Collection("WindowsShellSta")]
public class WindowsShellStaThreadTests
{
    [Fact]
    public async Task Cancelled_token_completes_canceled_without_running_work()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ran = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = WindowsShellStaThread.Instance.Run(() => Interlocked.Increment(ref ran), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Timeout_retires_the_STA_so_later_work_is_not_queued_behind_the_stall()
    {
        if (!OperatingSystem.IsWindows()) return;

        var previous = WindowsShellStaThread.CallTimeout;
        WindowsShellStaThread.CallTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var hung = WindowsShellStaThread.Instance;
            var stall = hung.Run(() => Thread.Sleep(2_000));
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => stall);
            Assert.Contains("retired", ex.Message, StringComparison.OrdinalIgnoreCase);

            var replacement = WindowsShellStaThread.Instance;
            Assert.NotSame(hung, replacement);

            var n = await replacement.Run(() => 7);
            Assert.Equal(7, n);
        }
        finally
        {
            WindowsShellStaThread.CallTimeout = previous;
        }
    }
}
