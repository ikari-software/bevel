using System;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 12: components get their OWN health budget. ShellHealthMonitor holds a CrashLoop
/// fault at 3 respawns and then stops bringing the role back — in Windows shell mode that means no
/// shell at all — so a crash-looping component must be quarantined locally and never counted there.
/// </summary>
public class ComponentHealthTests
{
    [Fact]
    public void Crashes_under_budget_retry()
    {
        var h = new ComponentHealth(crashBudget: 3);
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
    }

    [Fact]
    public void Exhausting_the_budget_quarantines_rather_than_respawning_forever()
    {
        var h = new ComponentHealth(crashBudget: 3);
        h.RecordCrash("a");
        h.RecordCrash("a");
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));   // stays quarantined
    }

    [Fact]
    public void One_components_budget_is_independent_of_another()
    {
        var h = new ComponentHealth(crashBudget: 3);
        h.RecordCrash("a"); h.RecordCrash("a"); h.RecordCrash("a");
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("b"));
    }

    // The heartbeat clock is INJECTED. Reading DateTime.UtcNow inside RecordHeartbeat while the test
    // compares against a fixed instant makes the test pass or fail depending on the wall clock —
    // a time bomb that expires the same day it is written.
    [Fact]
    public void A_hang_is_a_failure_even_though_nothing_crashed()
    {
        var h = new ComponentHealth(watchdogMs: 5000);
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        h.RecordHeartbeat("a", t0);
        Assert.Equal(ComponentVerdict.Quarantine, h.CheckWatchdog("a", t0.AddMilliseconds(6000)));
    }

    [Fact]
    public void A_live_heartbeat_keeps_the_component_healthy()
    {
        var h = new ComponentHealth(watchdogMs: 5000);
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        h.RecordHeartbeat("a", t0);
        Assert.Equal(ComponentVerdict.Healthy, h.CheckWatchdog("a", t0.AddMilliseconds(100)));
    }

    // budget 2, not 1: with budget 1 every crash quarantines immediately, so there is no Retry
    // left to observe after a Reset and the test could never pass.
    [Fact]
    public void Reset_clears_a_quarantine_so_the_user_can_re_enable()
    {
        var h = new ComponentHealth(crashBudget: 2);
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));
        h.Reset("a");
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
    }

    [Fact]
    public void An_unknown_instance_is_healthy_rather_than_throwing()
        => Assert.Equal(ComponentVerdict.Healthy, new ComponentHealth().CheckWatchdog("never-seen", DateTime.UtcNow));
}
