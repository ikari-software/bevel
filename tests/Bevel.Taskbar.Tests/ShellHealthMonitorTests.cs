using Bevel.App;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

public sealed class ShellHealthMonitorTests
{
    private static RoleHeartbeat Hb(
        ShellRole role, string stamp, HeartbeatStatus status,
        bool core = true, string error = "", bool stale = false, int proto = BuildStamp.Protocol) =>
        new(role, Pid: 1, stamp, proto, status, core, error, stale);

    private static ShellHealthMonitor Mon(string stamp = "1|ok|1", int ready = 3, int crash = 3, int skew = 2, int comm = 3) =>
        new(() => stamp, readyTimeoutTicks: ready, crashLoopLimit: crash, skewRestartBudget: skew, commTimeoutTicks: comm);

    [Fact]
    public void Alive_without_ready_stays_continue_until_timeout()
    {
        var m = Mon(ready: 3);
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting)).Action);
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting)).Action);
        var v = m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting));
        Assert.Equal(HealthAction.Restart, v.Action);
        Assert.Equal(HealthFault.StartupStuck, v.Fault);
    }

    [Fact]
    public void Stuck_restarts_exhaust_budget_then_hold()
    {
        var m = Mon(ready: 1, crash: 3);
        Assert.Equal(HealthAction.Restart, m.Observe(ShellRole.Taskbar, true, null).Action);
        Assert.Equal(HealthAction.Restart, m.Observe(ShellRole.Taskbar, true, null).Action);
        var v = m.Observe(ShellRole.Taskbar, true, null);
        Assert.Equal(HealthAction.Hold, v.Action);
        Assert.Equal(HealthFault.StartupStuck, v.Fault);
        Assert.True(m.IsHeld(ShellRole.Taskbar));
        Assert.Equal(HealthAction.Hold, m.Observe(ShellRole.Taskbar, true, null).Action); // latched
    }

    [Fact]
    public void Failed_heartbeat_holds_immediately_even_if_still_alive()
    {
        var m = Mon();
        var v = m.Observe(ShellRole.Taskbar, true,
            Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Failed, error: "XamlLoadException"));
        Assert.Equal(HealthAction.Hold, v.Action);
        Assert.Equal(HealthFault.StartupFailed, v.Fault);
        Assert.Contains("XamlLoadException", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_heartbeat_on_a_dead_child_holds_without_waiting_for_a_crash_loop()
    {
        var m = Mon();
        var v = m.Observe(ShellRole.Taskbar, false,
            Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Failed, error: "boom"));
        Assert.Equal(HealthAction.Hold, v.Action);
        Assert.Equal(HealthFault.StartupFailed, v.Fault);
    }

    [Fact]
    public void Crash_loop_holds_after_respawn_budget()
    {
        var m = Mon(crash: 3);
        m.Observe(ShellRole.Core, false, null); // first death, no respawn yet
        Assert.False(m.IsHeld(ShellRole.Core));
        m.NoteRespawn(ShellRole.Core);
        m.NoteRespawn(ShellRole.Core);
        m.NoteRespawn(ShellRole.Core);
        var v = m.Observe(ShellRole.Core, false, null);
        Assert.Equal(HealthAction.Hold, v.Action);
        Assert.Equal(HealthFault.CrashLoop, v.Fault);
    }

    [Fact]
    public void Stamp_mismatch_restarts_then_holds_after_budget()
    {
        var m = Mon("expected", skew: 2);
        var bad = Hb(ShellRole.Taskbar, "other", HeartbeatStatus.Ready);
        Assert.Equal(HealthAction.Restart, m.Observe(ShellRole.Taskbar, true, bad).Action);
        Assert.Equal(HealthFault.VersionSkew, m.Observe(ShellRole.Taskbar, true, bad).Fault);
        var v = m.Observe(ShellRole.Taskbar, true, bad);
        Assert.Equal(HealthAction.Hold, v.Action);
        Assert.Equal(HealthFault.VersionSkew, v.Fault);
    }

    [Fact]
    public void Protocol_mismatch_is_version_skew()
    {
        var m = Mon("1|ok|1");
        var v = m.Observe(ShellRole.Core, true,
            Hb(ShellRole.Core, "1|ok|1", HeartbeatStatus.Ready, proto: BuildStamp.Protocol + 1));
        Assert.Equal(HealthFault.VersionSkew, v.Fault);
        Assert.Equal(HealthAction.Restart, v.Action);
    }

    [Fact]
    public void Ready_taskbar_without_core_restarts_the_core()
    {
        var m = Mon(comm: 2);
        var hb = Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Ready, core: false);
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, hb).Action);
        var v = m.Observe(ShellRole.Taskbar, true, hb);
        Assert.Equal(HealthAction.Restart, v.Action);
        Assert.Equal(HealthFault.Communication, v.Fault);
        Assert.Equal(ShellRole.Core, v.Target);
    }

    [Fact]
    public void Ready_matching_stamp_resets_stuck_counters()
    {
        var m = Mon(ready: 2);
        m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting));
        var ready = Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Ready);
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, ready).Action);
        // Would have been Restart on the next starting tick if counters were not reset.
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting)).Action);
    }

    [Fact]
    public void Stale_ready_heartbeat_is_communication_restart()
    {
        var m = Mon();
        var v = m.Observe(ShellRole.Taskbar, true,
            Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Ready, stale: true));
        Assert.Equal(HealthAction.Restart, v.Action);
        Assert.Equal(HealthFault.Communication, v.Fault);
        Assert.Equal(ShellRole.Taskbar, v.Target);
    }

    [Fact]
    public void Reset_clears_a_hold()
    {
        var m = Mon();
        m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Failed, error: "x"));
        Assert.True(m.IsHeld(ShellRole.Taskbar));
        m.Reset(ShellRole.Taskbar);
        Assert.False(m.IsHeld(ShellRole.Taskbar));
        Assert.Equal(HealthAction.Continue, m.Observe(ShellRole.Taskbar, true, Hb(ShellRole.Taskbar, "1|ok|1", HeartbeatStatus.Starting)).Action);
    }
}
