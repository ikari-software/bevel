using System.Diagnostics;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Drives the Filer supervisor's policy (bevel-t48y) with a fake <see cref="IFilerProcess"/>, the
/// same seam <c>RoleProcessSupervisorTests</c> uses for role children: a cold open spawns and
/// supervises with the requested args; Quit teardown kills every instance without respawning;
/// RestartAll respawns each LIVE filer at its last-open-path; a crash (exit without a clean code)
/// respawns with backoff; a NORMAL window close (exit 0) NEVER respawns (the bevel-gdie user-hidden
/// rule); a failed spawn NACKs the open (the caller's fallback still gets the user a window)
/// instead of dying silently. Stderr capture is proven with a real child process — the pid-46808
/// crash was diagnosed ONLY because a nohup redirect caught its stderr by luck.
/// </summary>
public sealed class FilerSupervisorTests
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

    /// <summary>Liveness is a mutable field the test flips; <see cref="IFilerProcess.ExitCode"/> is
    /// what separates a user close (0) from a crash (anything else). Optionally throws on Start to
    /// exercise the crash-respawn backoff.</summary>
    private sealed class FakeFilerProcess : IFilerProcess
    {
        public volatile bool AliveField;
        public int? ExitCodeValue; // written BEFORE the volatile liveness flip; the monitor reads liveness first
        public bool FailOnStart;
        public int Pid;
        public int StartCount;
        public int KillCount;

        int IFilerProcess.Pid => Pid;

        public bool IsAlive => AliveField;
        public int? ExitCode => AliveField ? null : ExitCodeValue;

        public void Start()
        {
            Interlocked.Increment(ref StartCount);
            if (FailOnStart) throw new InvalidOperationException("simulated filer launch failure");
            AliveField = true;
        }

        public void Kill()
        {
            if (!AliveField) return; // production Kill is idempotent — a dead child can't be re-killed
            Interlocked.Increment(ref KillCount);
            AliveField = false;
        }

        public void Dispose() => Kill();

        /// <summary>Simulates the process dying on its own: the exit code is in place BEFORE the
        /// liveness flip so the monitor never observes a dead child with no verdict to read.</summary>
        public void Crash(int code = 17)
        {
            ExitCodeValue = code;
            AliveField = false;
        }

        public void CloseNormally()
        {
            ExitCodeValue = 0;
            AliveField = false;
        }
    }

    /// <summary>Records every (request, instanceKey) the supervisor asks for and hands out a fake
    /// per call — a respawn is a NEW process, but the supervisor decides when and with which
    /// request. <see cref="Next"/> lets a test arm a failing factory AFTER a healthy first open.</summary>
    private sealed class FakeFactory
    {
        public readonly List<(FilerSpawnRequest Request, long Key)> Calls = new();
        public readonly List<FakeFilerProcess> Made = new();
        public Func<FilerSpawnRequest, FakeFilerProcess>? Next;

        public IFilerProcess Create(FilerSpawnRequest request, long key)
        {
            lock (Calls) Calls.Add((request, key));
            var made = Next?.Invoke(request) ?? new FakeFilerProcess();
            made.Pid = 1000 + (int)key; // a process knows its pid only once spawned — key stands in
            lock (Made) Made.Add(made);
            return made;
        }
    }

    private static FilerSupervisor NewSupervisor(FakeFactory factory) =>
        new(Array.Empty<string>(), new Dictionary<string, string>(), factory.Create, pollInterval: Poll);

    [Fact]
    public async Task Cold_open_spawns_supervised_filer_with_requested_args()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();

        Assert.True(await sup.OpenAsync("/docs", search: true, selectPath: "/docs/x.txt"));
        Assert.True(await sup.OpenAsync("/pics"));

        lock (factory.Calls)
        {
            Assert.Equal(2, factory.Calls.Count);
            var (req1, key1) = factory.Calls[0];
            Assert.Equal("/docs", req1.OpenPath);
            Assert.True(req1.Search);
            Assert.Equal("/docs/x.txt", req1.SelectPath);
            Assert.Equal("/pics", factory.Calls[1].Request.OpenPath);
            Assert.False(factory.Calls[1].Request.Search);
            Assert.NotEqual(key1, factory.Calls[1].Key); // every open is its own instance
        }
    }

    [Fact]
    public async Task Failed_spawn_nacks_and_supervises_nothing()
    {
        var factory = new FakeFactory { Next = _ => new FakeFilerProcess { FailOnStart = true } };
        await using var sup = NewSupervisor(factory);
        sup.Start();

        // NACK (0) → the caller's fallback (LauncherFilerSpawner → ProcessFilerSpawner) still opens
        // a window; the supervisor must not poison itself with a child it could never start.
        Assert.False(await sup.OpenAsync("/docs"));

        await Task.Delay(100); // several poll ticks — nothing is supervised, so nothing retries
        lock (factory.Calls) Assert.Single(factory.Calls);
    }

    [Fact]
    public async Task Teardown_kills_every_filer_and_never_respawns()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        Assert.True(await sup.OpenAsync("/a"));
        Assert.True(await sup.OpenAsync("/b"));

        await sup.StopAsync();

        // Quit teardown: every child killed exactly once, and the monitor never comes back for
        // them (no closed-window resurrection, no crash-respawn of a child WE asked to die).
        await Task.Delay(100);
        lock (factory.Calls) Assert.Equal(2, factory.Calls.Count);
        lock (factory.Made) Assert.All(factory.Made, p => Assert.Equal(1, p.KillCount));
    }

    [Fact]
    public async Task RestartAll_respawns_live_filers_at_last_open_path()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        Assert.True(await sup.OpenAsync("/a", search: true));
        Assert.True(await sup.OpenAsync("/b"));

        await sup.RestartAllAsync();

        // Each live filer comes back on the CURRENT binary at the path it was last opened with —
        // including its find-mode flag. Fresh instance keys: a restart is a new process.
        await WaitFor(() => { lock (factory.Calls) return factory.Calls.Count == 4; },
            "RestartAll respawns both filers");
        lock (factory.Calls)
        {
            var paths = factory.Calls.Select(c => c.Request.OpenPath).OrderBy(p => p).ToList();
            Assert.Equal(new[] { "/a", "/a", "/b", "/b" }, paths);
            Assert.True(factory.Calls[2].Request.Search);
            Assert.Equal(4, factory.Calls.Select(c => c.Key).Distinct().Count());
        }
    }

    [Fact]
    public async Task Crash_respawns_at_last_path()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        var first = (FakeFilerProcess)await OpenAndGrabAsync(sup, factory, "/a");

        first.Crash(); // window was up, process died — crash, not a user close

        await WaitFor(() => { lock (factory.Calls) return factory.Calls.Count == 2; },
            "crash respawns the filer");
        lock (factory.Calls)
        {
            Assert.Equal("/a", factory.Calls[1].Request.OpenPath); // last-open-path
            Assert.Equal(factory.Calls[0].Key, factory.Calls[1].Key); // same logical instance
        }
    }

    [Fact]
    public async Task Normal_close_never_respawns()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        var first = (FakeFilerProcess)await OpenAndGrabAsync(sup, factory, "/a");

        first.CloseNormally(); // the user closed the window — bevel-gdie: NEVER bring it back

        await Task.Delay(150); // many poll ticks to prove the absence, not just a slow respawn
        lock (factory.Calls) Assert.Single(factory.Calls);
    }

    [Fact]
    public async Task Crash_respawn_backoff_doubles_and_does_not_spin_hot()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        var first = (FakeFilerProcess)await OpenAndGrabAsync(sup, factory, "/a");

        // From now on every respawn attempt fails at Start() — a crash-looping binary must cool
        // down (1, 2, 4… ticks), not spawn hot once per poll.
        factory.Next = _ => new FakeFilerProcess { FailOnStart = true };
        first.Crash();

        await WaitFor(() => { lock (factory.Calls) return factory.Calls.Count >= 2; },
            "at least one respawn attempt");
        await Task.Delay(250); // ~12 polls — hot spinning would make ~12 more attempts
        lock (factory.Calls) Assert.InRange(factory.Calls.Count, 2, 5);
    }

    // ── park + handoff (bevel-t48y Task 4) ─────────────────────────────────────────────────────

    /// <summary>Records every Show the supervisor sends (pid + open request) and answers on demand —
    /// the seam the production supervisor fills with a FilerControlChannel dial.</summary>
    private sealed class FakeShowSender
    {
        public readonly List<(int Pid, string OpenPath, bool Search, string? SelectPath)> Sent = new();
        public Func<int, Task<bool>> Reply = _ => Task.FromResult(true);

        public Task<bool> Send(int pid, string openPath, bool search, string? selectPath, CancellationToken ct)
        {
            lock (Sent) Sent.Add((pid, openPath, search, selectPath));
            return Reply(pid);
        }
    }

    [Fact]
    public async Task ParkAsync_spawns_one_parked_child()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();

        Assert.True(await sup.ParkAsync());

        lock (factory.Calls)
        {
            var (req, _) = Assert.Single(factory.Calls);
            Assert.True(req.Park);           // a parked spawn: no window, no open path
            Assert.Null(req.OpenPath);
        }
        lock (factory.Made) Assert.True(Assert.Single(factory.Made).IsAlive);
    }

    [Fact]
    public async Task OpenAsync_hands_off_to_the_parked_instance_and_reparks()
    {
        var factory = new FakeFactory();
        var sender = new FakeShowSender();
        await using var sup = new FilerSupervisor(Array.Empty<string>(), new Dictionary<string, string>(),
            factory.Create, pollInterval: Poll, showSender: sender.Send);
        sup.Start();
        Assert.True(await sup.ParkAsync());

        Assert.True(await sup.OpenAsync("/docs", search: true, selectPath: "/docs/x.txt"));

        // The Show went to the PARKED instance's pid, with the exact open request.
        lock (sender.Sent)
        {
            var (pid, path, search, select) = Assert.Single(sender.Sent);
            Assert.Equal(1001, pid); // the first spawned instance
            Assert.Equal("/docs", path);
            Assert.True(search);
            Assert.Equal("/docs/x.txt", select);
        }
        // Keep-1: the handoff immediately spawned a replacement park — and NO cold spawn happened
        // (the handoff replaced it). Two live children: the claimed window + the fresh park.
        lock (factory.Calls)
        {
            Assert.Equal(2, factory.Calls.Count);
            Assert.True(factory.Calls[1].Request.Park);
        }
        Assert.Equal(2, sup.LiveProcessesForTests().Count);
    }

    [Fact]
    public async Task Rapid_second_open_claims_the_replacement_never_the_same_instance()
    {
        var factory = new FakeFactory();
        var sender = new FakeShowSender();
        await using var sup = new FilerSupervisor(Array.Empty<string>(), new Dictionary<string, string>(),
            factory.Create, pollInterval: Poll, showSender: sender.Send);
        sup.Start();
        Assert.True(await sup.ParkAsync());

        Assert.True(await sup.OpenAsync("/a"));
        Assert.True(await sup.OpenAsync("/b"));

        // Both opens handed off — but each to a DIFFERENT parked instance (the second claimed the
        // replacement park, never the instance already mid-handoff). No cold spawn was needed.
        lock (sender.Sent)
            Assert.Equal(2, sender.Sent.Select(s => s.Pid).Distinct().Count());
        lock (factory.Calls)
            Assert.All(factory.Calls, c => Assert.True(c.Request.Park)); // park + repark + repark
    }

    [Fact]
    public async Task Show_failure_falls_back_to_cold_spawn_and_keeps_the_park()
    {
        var factory = new FakeFactory();
        var sender = new FakeShowSender { Reply = _ => Task.FromResult(false) }; // dial failed
        await using var sup = new FilerSupervisor(Array.Empty<string>(), new Dictionary<string, string>(),
            factory.Create, pollInterval: Poll, showSender: sender.Send);
        sup.Start();
        Assert.True(await sup.ParkAsync());

        Assert.True(await sup.OpenAsync("/docs")); // the fallback cold spawn still opens the window

        lock (factory.Calls)
        {
            Assert.Equal(2, factory.Calls.Count);
            Assert.False(factory.Calls[1].Request.Park); // cold spawn at the requested path
            Assert.Equal("/docs", factory.Calls[1].Request.OpenPath);
        }
        // The parked instance was NOT consumed by the failed handoff — it stays parked (keep-1) and
        // still alive, ready for the next open.
        lock (factory.Made) Assert.True(factory.Made[0].IsAlive);
    }

    [Fact]
    public async Task Parked_crash_respawns_a_park_not_a_window()
    {
        var factory = new FakeFactory();
        await using var sup = NewSupervisor(factory);
        sup.Start();
        Assert.True(await sup.ParkAsync());
        FakeFilerProcess parked;
        lock (factory.Made) parked = factory.Made[0];

        parked.Crash(137); // the hidden pre-warm died — keep-1 needs a fresh PARK, not a window

        await WaitFor(() => { lock (factory.Calls) return factory.Calls.Count == 2; },
            "a crashed park is re-parked");
        lock (factory.Calls) Assert.True(factory.Calls[1].Request.Park);
    }

    [Fact]
    public async Task RestartAll_reparks_fresh_and_respawns_live_filers_at_their_paths()
    {
        var factory = new FakeFactory();
        var sender = new FakeShowSender();
        await using var sup = new FilerSupervisor(Array.Empty<string>(), new Dictionary<string, string>(),
            factory.Create, pollInterval: Poll, showSender: sender.Send);
        sup.Start();
        Assert.True(await sup.ParkAsync());
        Assert.True(await sup.OpenAsync("/docs")); // handoff: claimed window + replacement park

        await sup.RestartAllAsync();

        // 4 spawns total: park, replacement park, the live window's restart (at its SHOWN path —
        // the claim recorded it), and the park's fresh re-park (never a re-show of a stale path).
        await WaitFor(() => { lock (factory.Calls) return factory.Calls.Count == 4; },
            "RestartAll respawns the claimed window and re-parks");
        lock (factory.Calls)
        {
            Assert.Equal("/docs", factory.Calls[2].Request.OpenPath); // claimed window restarts at its path
            Assert.False(factory.Calls[2].Request.Park);
            Assert.True(factory.Calls[3].Request.Park); // the park re-parks — fresh, path-less
        }
    }

    private static async Task<IFilerProcess> OpenAndGrabAsync(FilerSupervisor sup, FakeFactory factory, string path)
    {
        Assert.True(await sup.OpenAsync(path));
        lock (factory.Calls)
            Assert.Single(factory.Calls);
        // The fake the supervisor currently holds is the one the factory just made; grab it via a
        // probe of the supervisor's live set (the tests drive liveness through it).
        return sup.LiveProcessesForTests().Single();
    }

    [Fact]
    public async Task FilerProcess_captures_stderr_to_per_child_file()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return; // POSIX-only: /bin/sh

        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("echo boom 1>&2");
        using var child = new FilerProcess(startInfo);
        child.Start();

        await WaitFor(() => !child.IsAlive, "the child exits");
        await WaitFor(() => File.Exists(child.StderrLogPath) &&
                       File.ReadAllText(child.StderrLogPath).Contains("boom"),
            "the child's stderr lands in the per-child log file");
    }
}
