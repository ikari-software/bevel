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

    /// <summary>What a raw connect to <paramref name="path"/> reports, for failure messages: the next CI
    /// run says WHICH SocketError an OS returned instead of a bare "expected True".</summary>
    private static string ConnectOutcome(string path)
    {
        if (!File.Exists(path)) return "path does not exist";
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            s.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            return "connect succeeded";
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return $"connect failed: {ex.SocketErrorCode} (native {ex.NativeErrorCode})";
        }
    }

    [Fact]
    public void A_stale_socket_is_not_reported()
    {
        var path = NewSocketPath();
        UdsSocketClaim.BindListener(path, backlog: 4).Dispose();
        try
        {
            // Unix: disposing the raw listener leaves the file (a crashed owner's leftover). Windows: the
            // managed Dispose deletes it, so "stale" there is the absent path — same pattern as
            // UdsSocketClaimTests.IsServing_distinguishes_absent_stale_and_live. Assert the precondition
            // per OS instead of assuming Unix.
            Assert.Equal(!OperatingSystem.IsWindows(), File.Exists(path));
            Assert.True(IncumbentProbe.FirstLiveSocket(path) is null,
                $"stale/absent socket was reported live; {ConnectOutcome(path)}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_plain_file_at_the_socket_path_is_not_reported()
    {
        // "Not a socket" on every OS: only a connect that SUCCEEDS may prove a live incumbent.
        var path = NewSocketPath();
        File.WriteAllText(path, "not a socket");
        try
        {
            Assert.True(IncumbentProbe.FirstLiveSocket(path) is null,
                $"a plain file was reported as a live incumbent; {ConnectOutcome(path)}");
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
            var msg = IncumbentProbe.Decide(NewSocketPath(), core, Path.Combine(Path.GetTempPath(), "x", "bevel-recover.sh"));
            Assert.NotNull(msg);
            Assert.Contains(core, msg);
            Assert.Contains(Path.Combine(Path.GetTempPath(), "x", "bevel-recover.sh"), msg);
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
            Assert.Equal(script, IncumbentProbe.RecoverScriptPath(Path.Combine(root, "Contents", "MacOS") + Path.DirectorySeparatorChar));
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
