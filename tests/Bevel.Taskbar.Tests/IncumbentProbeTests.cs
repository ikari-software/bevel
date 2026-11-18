using Bevel.App.Supervision;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-9790: the launcher decides "another Bevel is already serving" BEFORE it spawns any child.
/// <see cref="IncumbentProbe"/> is that decision over real Unix sockets in a temp dir (short paths —
/// macOS <c>sun_path</c> is 104 bytes).
/// </summary>
public sealed class IncumbentProbeTests
{
    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlinc-{Guid.NewGuid():N}"[..15] + ".sock");

    [Fact]
    public void A_live_listener_is_reported()
    {
        var path = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(path, backlog: 4);
        try
        {
            Assert.Equal(path, IncumbentProbe.FirstLiveSocket(path));
        }
        finally { UdsSocketClaim.ReleaseListener(listener, path); }
    }

    [Fact]
    public void A_stale_socket_file_is_not_reported()
    {
        var path = NewSocketPath();
        // Disposing a raw bound listener leaves the file behind: what a crashed owner leaves.
        UdsSocketClaim.BindListener(path, backlog: 4).Dispose();
        try
        {
            Assert.True(File.Exists(path));
            Assert.Null(IncumbentProbe.FirstLiveSocket(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_path_is_not_reported()
    {
        Assert.Null(IncumbentProbe.FirstLiveSocket(NewSocketPath()));
    }

    [Fact]
    public void The_first_live_path_wins_when_several_are_given()
    {
        var stale = NewSocketPath();
        UdsSocketClaim.BindListener(stale, backlog: 4).Dispose();
        var live = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(live, backlog: 4);
        try
        {
            Assert.Equal(live, IncumbentProbe.FirstLiveSocket(NewSocketPath(), stale, live));
        }
        finally { UdsSocketClaim.ReleaseListener(listener, live); File.Delete(stale); }
    }
}

/// <summary>bevel-9790 stand-down decision: pure, no spawn, no quit-marker write; the alert is a seam.</summary>
public sealed class IncumbentStandDownTests
{
    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlsd-{Guid.NewGuid():N}"[..14] + ".sock");

    [Fact]
    public void A_live_core_socket_stands_down_naming_the_socket_and_script()
    {
        var core = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(core, backlog: 4);
        try
        {
            var markerBefore = Bevel.App.Supervision.QuitRequest.Exists();
            var msg = IncumbentProbe.Decide(NewSocketPath(), core, "/x/bevel-recover.sh");
            Assert.NotNull(msg);
            Assert.Contains(core, msg);
            Assert.Contains("/x/bevel-recover.sh", msg);
            Assert.Equal(markerBefore, Bevel.App.Supervision.QuitRequest.Exists()); // read-only decision
            Assert.Equal(3, IncumbentProbe.StandDownExitCode);
        }
        finally { UdsSocketClaim.ReleaseListener(listener, core); }
    }

    [Fact]
    public void Stale_and_missing_sockets_proceed()
    {
        var stale = NewSocketPath();
        UdsSocketClaim.BindListener(stale, backlog: 4).Dispose();
        try { Assert.Null(IncumbentProbe.Decide(stale, NewSocketPath(), "s")); }
        finally { File.Delete(stale); }
    }

    [Fact]
    public void Announce_logs_and_raises_the_alert_once()
    {
        var logs = new List<string>(); var alerts = new List<(string, string)>();
        IncumbentProbe.Announce("boom", logs.Add, (t, b) => alerts.Add((t, b)));
        Assert.Equal(["boom"], logs);
        var (title, body) = Assert.Single(alerts);
        Assert.Equal("Bevel", title);
        Assert.Contains("boom", body);
    }

    [Fact]
    public void A_throwing_alert_is_logged_not_propagated()
    {
        var logs = new List<string>();
        IncumbentProbe.Announce("boom", logs.Add, (_, _) => throw new InvalidOperationException("no gui"));
        Assert.Contains(logs, l => l.Contains("no gui"));
    }

    [Fact]
    public void Recover_script_prefers_the_bundled_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "bvlrs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(root, "Contents", "Resources"));
        var script = Path.Combine(root, "Contents", "Resources", "bevel-recover.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        try
        {
            Assert.Equal(script, IncumbentProbe.RecoverScriptPath(Path.Combine(root, "Contents", "MacOS") + "/"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Recover_script_falls_back_to_the_repo_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "bvlrs-" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Path.Combine(root, "src", "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(root, "packaging", "macos"));
        var script = Path.Combine(root, "packaging", "macos", "bevel-recover.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        try { Assert.Equal(script, IncumbentProbe.RecoverScriptPath(bin)); }
        finally { Directory.Delete(root, true); }
    }
}
