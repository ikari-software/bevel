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

        /// <summary>Fired whenever the monitor reads liveness and finds the child dead — lets a test
        /// inject a mid-tick stop exactly at the point the bevel-ply race opens.</summary>
        public Action? OnObservedDead;

        public bool IsAlive
        {
            get
            {
                if (!Alive) OnObservedDead?.Invoke();
                return Alive;
            }
        }

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
    public async Task Does_not_respawn_a_child_when_stop_latches_mid_tick()
    {
        // bevel-ply: a kill -TERM fan-out hits the launcher AND its children at once. A child that
        // exits cleanly on its own SIGTERM looks "died" to the monitor tick; if the stop latch flips
        // AFTER the liveness check but BEFORE the respawn, the old code started a fresh PID that never
        // got the signal — the shell "wouldn't die" without SIGKILL. Reproduce that exact window by
        // latching the stop the instant the monitor observes the child dead.
        var log = new List<string>();
        RoleProcessSupervisor? sup = null;
        var core = new FakeRoleProcess(ShellRole.Core, log);
        core.OnObservedDead = () => sup!.RequestStop();
        await using var s = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        sup = s;
        await s.StartAsync();
        Assert.Equal(1, core.StartCount);

        core.Alive = false; // clean shutdown-signal exit that reads as a "crash" to the tick

        // Several ticks pass; with the guard the monitor observes dead → RequestStop latches → break,
        // so it must NEVER respawn. Without the fix a second (orphan) PID would be started.
        await Task.Delay(Poll * 8);
        Assert.Equal(1, core.StartCount);
    }

    [Fact]
    public async Task SpawnRoleAsync_adds_and_supervises_a_new_child()
    {
        // bevel-gdie: Start ▸ "Show Desktop" appends a desktop child at runtime. It must start AND be
        // picked up by the crash-monitor like any boot-time child.
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core, taskbar }, Poll);
        await sup.StartAsync();
        Assert.False(await sup.IsRoleRunningAsync(ShellRole.Desktop));

        var desktop = new FakeRoleProcess(ShellRole.Desktop, log);
        await sup.SpawnRoleAsync(ShellRole.Desktop, () => desktop);

        Assert.True(desktop.IsAlive);
        Assert.Equal(1, desktop.StartCount);
        Assert.True(await sup.IsRoleRunningAsync(ShellRole.Desktop));

        // Supervised: a crash is respawned by the monitor (proves it joined the set, not a detached spawn).
        desktop.Alive = false;
        await WaitFor(() => desktop.StartCount == 2, "the monitor should respawn the runtime-added desktop");
    }

    [Fact]
    public async Task SpawnRoleAsync_is_idempotent()
    {
        // A double-click on "Show Desktop" must not spawn two desktops.
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync();

        var first = new FakeRoleProcess(ShellRole.Desktop, log);
        var secondFactoryCalls = 0;
        await sup.SpawnRoleAsync(ShellRole.Desktop, () => first);
        await sup.SpawnRoleAsync(ShellRole.Desktop, () => { secondFactoryCalls++; return new FakeRoleProcess(ShellRole.Desktop, log); });

        Assert.Equal(0, secondFactoryCalls);            // second request ignored — factory never invoked
        Assert.Equal(1, first.StartCount);              // the one desktop wasn't restarted
        Assert.True(await sup.IsRoleRunningAsync(ShellRole.Desktop));
    }

    [Fact]
    public async Task CloseRoleAsync_removes_the_child_and_never_respawns_it()
    {
        // bevel-gdie: "Hide Desktop" tears the desktop down AND de-supervises it — a user-hidden surface
        // must stay hidden, not be auto-restarted by the crash-monitor.
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync();

        var desktop = new FakeRoleProcess(ShellRole.Desktop, log);
        await sup.SpawnRoleAsync(ShellRole.Desktop, () => desktop);
        Assert.True(await sup.IsRoleRunningAsync(ShellRole.Desktop));

        await sup.CloseRoleAsync(ShellRole.Desktop);
        Assert.False(desktop.IsAlive);
        Assert.True(desktop.KillCount >= 1);
        Assert.False(await sup.IsRoleRunningAsync(ShellRole.Desktop));

        // Several monitor ticks pass — the removed desktop must NOT be respawned (StartCount frozen at 1).
        var startsAtClose = desktop.StartCount;
        await Task.Delay(Poll * 8);
        Assert.Equal(startsAtClose, desktop.StartCount);
        Assert.False(await sup.IsRoleRunningAsync(ShellRole.Desktop));

        // The core (untouched) is still supervised — closing one child doesn't disturb the rest.
        Assert.True(await sup.IsRoleRunningAsync(ShellRole.Core));
    }

    [Fact]
    public async Task CloseRoleAsync_is_a_noop_for_an_unsupervised_role()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(new IRoleProcess[] { core }, Poll);
        await sup.StartAsync();

        await sup.CloseRoleAsync(ShellRole.Desktop);   // never spawned — must not throw
        Assert.True(await sup.IsRoleRunningAsync(ShellRole.Core));
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

    [Fact]
    public async Task Restarts_a_live_core_whose_health_probe_fails()
    {
        // bevel-hprv: a core process that is still running but whose socket is gone must be
        // treated as dead. The probe fails for 3 consecutive ticks, then Kill+Start.
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var healthy = true;
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { core }, Poll,
            coreHealthyProbe: _ => Task.FromResult(healthy));
        await sup.StartAsync();
        Assert.Equal(1, core.StartCount);

        healthy = false;
        await WaitFor(() => core.StartCount >= 2 && core.KillCount >= 1,
            "supervisor should kill+respawn a live-but-unhealthy core");
    }

    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlsup-{Guid.NewGuid():N}"[..15] + ".sock");

    [Fact]
    public async Task Restarts_a_core_whose_socket_is_served_under_a_foreign_nonce()
    {
        // bevel-wio0 × bevel-hprv: after a hijack core.sock EXISTS and even ACCEPTS connections — it
        // just belongs to the wrong core. The old File.Exists probe certified that as healthy. The
        // production probe (CoreSocketProbe, an authenticated handshake with the launcher's nonce)
        // must read it as unhealthy so the supervisor kills + respawns.
        var path = NewSocketPath();
        var ourNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var foreignNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        await using var hijacker = new Bevel.ShellCore.Ipc.UdsMessageServer(
            path, foreignNonce, (_, _, _) => ValueTask.FromResult(Array.Empty<byte>()));
        hijacker.Start();
        Assert.True(File.Exists(path)); // the check the old probe would have passed

        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { core }, Poll,
            coreHealthyProbe: ct => CoreSocketProbe.IsServingAsync(path, ourNonce, ct));
        await sup.StartAsync();

        await WaitFor(() => core.KillCount >= 1 && core.StartCount >= 2,
            "a connectable socket under a foreign nonce must not be certified as a healthy core");
    }

    [Fact]
    public async Task Keeps_a_core_whose_socket_answers_with_the_session_nonce()
    {
        var path = NewSocketPath();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        await using var ours = new Bevel.ShellCore.Ipc.UdsMessageServer(
            path, nonce, (_, _, _) => ValueTask.FromResult(Array.Empty<byte>()));
        ours.Start();

        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { core }, Poll,
            coreHealthyProbe: ct => CoreSocketProbe.IsServingAsync(path, nonce, ct));
        await sup.StartAsync();

        await Task.Delay(Poll * 10); // well past the 3-tick unhealthy threshold
        Assert.Equal(0, core.KillCount);
        Assert.Equal(1, core.StartCount);
        Assert.Equal(0, ours.ClientCount); // the probe never lingers as a client
    }

    [Fact]
    public async Task Does_not_respawn_when_quit_is_requested()
    {
        // bevel-0md2: Start ▸ Quit writes a marker; a child that then exits must stay dead.
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        var quit = false;
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { core, taskbar }, Poll,
            quitRequested: () => quit);
        await sup.StartAsync();

        quit = true;
        taskbar.Alive = false;
        await Task.Delay(Poll * 8);
        Assert.Equal(1, taskbar.StartCount); // never respawned
        Assert.True(core.IsAlive);           // unrelated children are not killed by the marker itself
    }

    private static RoleHeartbeat Hb(ShellRole role, HeartbeatStatus status, string stamp = "s", bool core = true, string error = "") =>
        new(role, 1, stamp, BuildStamp.Protocol, status, core, error);

    [Fact]
    public async Task Holds_and_does_not_respawn_a_child_that_reports_startup_failure()
    {
        var log = new List<string>();
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        var alerts = new List<HealthVerdict>();
        RoleHeartbeat? hb = Hb(ShellRole.Taskbar, HeartbeatStatus.Starting);
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { taskbar }, Poll,
            health: new ShellHealthMonitor(() => "s"),
            heartbeat: _ => hb,
            onAlert: v => { lock (alerts) alerts.Add(v); });
        await sup.StartAsync();
        Assert.Equal(1, taskbar.StartCount);

        hb = Hb(ShellRole.Taskbar, HeartbeatStatus.Failed, error: "XamlLoadException");
        taskbar.Alive = false;

        await WaitFor(() => { lock (alerts) return alerts.Count > 0; }, "hold should surface an alert");
        await Task.Delay(Poll * 6);
        Assert.Equal(1, taskbar.StartCount); // held — not crash-looped
        lock (alerts) Assert.Equal(HealthFault.StartupFailed, alerts[0].Fault);
    }

    [Fact]
    public async Task Restarts_a_live_child_whose_stamp_does_not_match()
    {
        var log = new List<string>();
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { taskbar }, Poll,
            health: new ShellHealthMonitor(() => "new", skewRestartBudget: 3),
            heartbeat: _ => Hb(ShellRole.Taskbar, HeartbeatStatus.Ready,
                stamp: taskbar.StartCount >= 2 ? "new" : "old"));
        await sup.StartAsync();

        await WaitFor(() => taskbar.KillCount >= 1 && taskbar.StartCount >= 2,
            "version skew should kill+respawn the live child onto the current binary");

        var starts = taskbar.StartCount;
        await Task.Delay(Poll * 6);
        Assert.Equal(starts, taskbar.StartCount); // matching stamp after respawn — no more restarts
        Assert.True(taskbar.IsAlive);
    }

    [Fact]
    public async Task Ready_taskbar_without_a_core_link_restarts_the_core()
    {
        var log = new List<string>();
        var core = new FakeRoleProcess(ShellRole.Core, log);
        var taskbar = new FakeRoleProcess(ShellRole.Taskbar, log);
        RoleHeartbeat HbFor(ShellRole role) => role == ShellRole.Taskbar
            ? Hb(ShellRole.Taskbar, HeartbeatStatus.Ready, core: core.StartCount >= 2)
            : Hb(ShellRole.Core, HeartbeatStatus.Ready);
        await using var sup = new RoleProcessSupervisor(
            new IRoleProcess[] { core, taskbar }, Poll,
            health: new ShellHealthMonitor(() => "s", commTimeoutTicks: 2),
            heartbeat: r => HbFor(r));
        await sup.StartAsync();
        Assert.Equal(1, core.StartCount);

        await WaitFor(() => core.KillCount >= 1 && core.StartCount >= 2,
            "lost IPC should restart the core, not the ready taskbar");
        Assert.Equal(0, taskbar.KillCount);
        Assert.Equal(1, taskbar.StartCount);
        Assert.True(taskbar.IsAlive);
    }
}
