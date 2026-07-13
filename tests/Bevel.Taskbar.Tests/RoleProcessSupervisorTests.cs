using Bevel.App;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Drives the multi-process supervisor's policy (bevel-gww.4) with a fake <see cref="IRoleProcess"/>,
/// so startup ordering, crash-restart, and the quit/restart fan-out are asserted without launching real
/// OS processes: the shell-core owner starts before the UI; a crashed child is respawned; RestartAll
/// cycles everything; RestartCore swaps only the core; and Stop halts the monitor for good.
/// </summary>
public sealed class RoleProcessSupervisorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), because);
    }

    /// <summary>Records start/kill order and counts; liveness is a mutable field a test flips to
    /// simulate a crash. Optionally throws on Start to exercise the crash-respawn backoff.</summary>
    private sealed class FakeRoleProcess : IRoleProcess
    {
        private readonly List<string> _log;
        public ShellRole Role { get; }
        public volatile bool Alive;
        public volatile bool FailOnStart;
        public int StartCount;
        public int KillCount;

        public FakeRoleProcess(ShellRole role, List<string> log) { Role = role; _log = log; }

        public bool IsAlive => Alive;

        public void Start()
        {
            Interlocked.Increment(ref StartCount);
            lock (_log) _log.Add($"start:{Role}");
            if (FailOnStart) throw new InvalidOperationException("simulated launch failure");
            Alive = true;
        }

        public void Kill()
        {
            Interlocked.Increment(ref KillCount);
            lock (_log) _log.Add($"kill:{Role}");
            Alive = false;
        }

        public void Dispose() => Kill();
    }

    [Fact]
    public async Task Starts_core_before_ui()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core, taskbar }, Poll);

        await sup.StartAsync();

        Assert.True(core.IsAlive);
        Assert.True(taskbar.IsAlive);
        string[] startOrder;
        lock (log) startOrder = log.ToArray();
        Assert.Equal(new[] { "start:Core", "start:Taskbar" }, startOrder); // dependency order
    }

    [Fact]
    public async Task Respawns_a_crashed_child()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync();
        Assert.Equal(1, core.StartCount);

        core.Alive = false; // crash

        await WaitFor(() => core.StartCount == 2, "the monitor loop should respawn the crashed core");
        Assert.True(core.IsAlive);
    }

    [Fact]
    public async Task RestartAll_cycles_every_process()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core, taskbar }, Poll);
        await sup.StartAsync();

        await sup.RestartAllAsync();

        Assert.Equal(2, core.StartCount);      // each respawned exactly once...
        Assert.Equal(2, taskbar.StartCount);
        Assert.True(core.KillCount >= 1);      // ...after being killed
        Assert.True(taskbar.KillCount >= 1);
        Assert.True(core.IsAlive);
        Assert.True(taskbar.IsAlive);
    }

    [Fact]
    public async Task RestartCore_swaps_only_the_core()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core, taskbar }, Poll);
        await sup.StartAsync();

        await sup.RestartCoreAsync();

        Assert.Equal(2, core.StartCount);       // core cycled
        Assert.Equal(1, core.KillCount);
        Assert.Equal(1, taskbar.StartCount);    // taskbar untouched
        Assert.Equal(0, taskbar.KillCount);
    }

    [Fact]
    public async Task Stop_kills_all_and_halts_the_monitor()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync();

        await sup.StopAsync();
        Assert.False(core.IsAlive);

        // After stop, a crash must NOT be respawned — the loop is gone.
        var startsAtStop = core.StartCount;
        core.Alive = false;
        await Task.Delay(Poll * 6);
        Assert.Equal(startsAtStop, core.StartCount);
    }

    [Fact]
    public async Task Keeps_retrying_a_child_that_fails_to_launch()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync(); // first start succeeds

        core.FailOnStart = true; // every respawn now throws
        core.Alive = false;

        // The guarded loop must survive repeated launch failures and keep attempting (with backoff),
        // never tearing the monitor down.
        await WaitFor(() => core.StartCount >= 3, "supervisor should keep retrying a failing child");
    }
}
