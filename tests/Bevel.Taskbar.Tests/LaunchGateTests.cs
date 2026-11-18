using System.Diagnostics;
using Bevel.App.Supervision;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-9790 round 2: the launcher's pre-spawn gate with spy collaborators, the bounded
/// dialog runner, and a hermetic recover-script search. No real dialog, no real marker write.</summary>
public sealed class LaunchGateTests
{
    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlgt-{Guid.NewGuid():N}"[..14] + ".sock");

    [Fact]
    public void A_live_socket_returns_3_and_calls_neither_clear_nor_spawn()
    {
        var core = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(core, backlog: 4);
        var calls = new List<string>();
        try
        {
            var exit = IncumbentProbe.Gate(NewSocketPath(), core, "/x/recover.sh",
                log: m => calls.Add("log"), alert: (_, _) => calls.Add("alert"),
                clearQuitMarker: () => calls.Add("clear"), spawnChildren: () => calls.Add("spawn"));
            Assert.Equal(3, exit);
            Assert.Equal(["log", "alert"], calls);   // announced, but nothing cleared or spawned
        }
        finally { UdsSocketClaim.ReleaseListener(listener, core); }
    }

    [Fact]
    public void A_live_control_socket_also_stands_down()
    {
        var control = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(control, backlog: 4);
        var calls = new List<string>();
        try
        {
            Assert.Equal(3, IncumbentProbe.Gate(control, NewSocketPath(), "s", _ => { }, (_, _) => { },
                () => calls.Add("clear"), () => calls.Add("spawn")));
            Assert.Empty(calls);
        }
        finally { UdsSocketClaim.ReleaseListener(listener, control); }
    }

    [Fact]
    public void Stale_and_missing_sockets_proceed_clearing_then_spawning_in_order()
    {
        var stale = NewSocketPath();
        UdsSocketClaim.BindListener(stale, backlog: 4).Dispose();
        var calls = new List<string>();
        try
        {
            var exit = IncumbentProbe.Gate(stale, NewSocketPath(), "s",
                log: _ => calls.Add("log"), alert: (_, _) => calls.Add("alert"),
                clearQuitMarker: () => calls.Add("clear"), spawnChildren: () => calls.Add("spawn"));
            Assert.Null(exit);
            Assert.Equal(["clear", "spawn"], calls);
        }
        finally { File.Delete(stale); }
    }

    [Fact]
    public void A_timed_out_dialog_process_is_killed_not_orphaned()
    {
        var psi = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        psi.ArgumentList.Add("30");
        var completed = ShellHealthAlert.RunBounded(psi, TimeSpan.FromMilliseconds(300), killOnTimeout: true, out var pid);
        Assert.False(completed);
        Assert.True(pid > 0);
        var dead = SpinWait.SpinUntil(() =>
        {
            try { return Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return true; }
        }, TimeSpan.FromSeconds(5));
        Assert.True(dead, "the timed-out child must be killed");
    }

    [Fact]
    public void The_hold_path_leaves_a_timed_out_dialog_up()
    {
        var psi = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        psi.ArgumentList.Add("30");
        Assert.False(ShellHealthAlert.RunBounded(psi, TimeSpan.FromMilliseconds(200), killOnTimeout: false, out var pid));
        var proc = Process.GetProcessById(pid);
        try { Assert.False(proc.HasExited); }
        finally { proc.Kill(); }
    }

    [Fact]
    public void A_chatty_dialog_process_does_not_stall_on_a_full_pipe()
    {
        // Redirected like the osascript caller: an undrained pipe fills at ~64 KB and stalls the child.
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("head -c 1000000 /dev/zero | tr '\\0' x; head -c 1000000 /dev/zero | tr '\\0' y 1>&2");
        Assert.True(ShellHealthAlert.RunBounded(psi, TimeSpan.FromSeconds(10), killOnTimeout: true, out _));
    }

    [Fact]
    public void Recover_script_search_stops_at_the_ceiling()
    {
        var outer = Path.Combine(Path.GetTempPath(), "bvlrc-" + Guid.NewGuid().ToString("N")[..8]);
        var ceiling = Path.Combine(outer, "repo");
        var bin = Path.Combine(ceiling, "bin");
        Directory.CreateDirectory(bin);
        // A decoy ABOVE the ceiling must never be found.
        Directory.CreateDirectory(Path.Combine(outer, "packaging", "macos"));
        File.WriteAllText(Path.Combine(outer, "packaging", "macos", "bevel-recover.sh"), "#!/bin/sh\n");
        try
        {
            Assert.Equal(Path.Combine("packaging", "macos", "bevel-recover.sh"),
                IncumbentProbe.RecoverScriptPath(bin, ceiling));
        }
        finally { Directory.Delete(outer, true); }
    }
}
