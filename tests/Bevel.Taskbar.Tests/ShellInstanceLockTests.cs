using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The launcher's single-instance guard (bevel-wio0). Two launchers against one lock path leave
/// exactly one shell: the second reports the incumbent and does not start; a dead/finished owner's
/// lock is reclaimed; leftovers from a crash never block. Exercised against a real file lock — flock
/// contends between two open descriptors even inside one process, so no child process is needed.
/// </summary>
public sealed class ShellInstanceLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bvl-lock-" + Guid.NewGuid().ToString("N")[..8]);
    private string LockPath => Path.Combine(_dir, "shell.lock");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Second_launcher_reports_the_incumbent_and_does_not_start()
    {
        using var first = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out _);
        Assert.NotNull(first);

        using var second = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out var incumbent);

        Assert.Null(second);                              // exactly one shell
        Assert.Equal(Environment.ProcessId, incumbent);   // and it can say which
    }

    [Fact]
    public void Lock_is_reclaimed_once_the_incumbent_is_gone()
    {
        var first = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out _);
        Assert.NotNull(first);
        first.Dispose(); // a crash releases the flock exactly the same way — the kernel drops it

        using var second = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out var incumbent);

        Assert.NotNull(second);
        Assert.Equal(0, incumbent);
        Assert.Equal(Environment.ProcessId, ShellInstanceLock.ReadIncumbentPid(LockPath));
    }

    [Fact]
    public async Task Waits_for_a_predecessor_that_is_still_tearing_down()
    {
        // pkill-then-relaunch scripts land while the old launcher is mid-teardown; a bounded wait
        // turns "already running" into a successful start instead of a ghost report.
        var first = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out _);
        Assert.NotNull(first);
        var release = Task.Run(async () => { await Task.Delay(250); first.Dispose(); });

        using var second = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.FromSeconds(5), out _);
        await release;

        Assert.NotNull(second);
    }

    [Fact]
    public void Leftover_files_from_a_crashed_launcher_do_not_block()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LockPath, "");            // unlocked leftover
        File.WriteAllText(LockPath + ".pid", "999999"); // stale pid of a process long gone

        using var held = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out _);

        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId, ShellInstanceLock.ReadIncumbentPid(LockPath)); // pid re-recorded
    }

    [Fact]
    public void Release_removes_the_pid_file_so_no_stale_pid_is_ever_reported()
    {
        var held = ShellInstanceLock.TryAcquire(LockPath, TimeSpan.Zero, out _);
        Assert.NotNull(held);
        Assert.True(File.Exists(LockPath + ".pid"));

        held.Dispose();

        Assert.False(File.Exists(LockPath + ".pid"));
        Assert.Equal(0, ShellInstanceLock.ReadIncumbentPid(LockPath));
    }
}
