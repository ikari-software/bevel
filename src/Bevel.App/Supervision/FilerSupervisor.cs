using System.Collections.Concurrent;
using Bevel.App.ShellCore;

namespace Bevel.App.Supervision;

/// <summary>What a supervised Filer is opened with — and what it is RESPAWNED with (crash policy:
/// restart at last-open-path, never with window state). Immutable per logical instance. A PARKED
/// spawn (<see cref="Park"/>) carries no path: the window is built hidden at Home and is told its
/// path only by the <c>Show</c> handoff.</summary>
internal sealed record FilerSpawnRequest(string? OpenPath, bool Search, string? SelectPath, bool Park = false);

/// <summary>
/// One supervised Filer process. The supervisor's test seam (the <c>IRoleProcess</c> analogue):
/// <see cref="ExitCode"/> after death is the close-vs-crash verdict — 0 is the window's normal
/// close (the user hid the surface; <c>bevel-gdie</c>: never respawn), anything else (or null for
/// a hard kill) is a crash. Mechanics live behind this interface so the supervisor's policy is
/// driven deterministically in <c>FilerSupervisorTests</c> without real OS processes.
/// </summary>
internal interface IFilerProcess : IDisposable
{
    /// <summary>True while the process is running.</summary>
    bool IsAlive { get; }

    /// <summary>This child's OS pid — the <c>filer-&lt;pid&gt;.sock</c> the launcher dials for the
    /// parked <c>Show</c> handoff. 0 before the first <see cref="Start"/>.</summary>
    int Pid { get; }

    /// <summary>The child's exit code once it has exited, null while alive (or if unknown —
    /// a hard-killed child leaves no readable code and reads as a crash).</summary>
    int? ExitCode { get; }

    /// <summary>Spawns the process. Throws on launch failure — the caller decides policy.</summary>
    void Start();

    /// <summary>Stops the process (graceful first, then hard) if alive. Safe when not running.</summary>
    void Kill();
}

/// <summary>
/// Supervises the shell's Filer windows (bevel-t48y). A SIBLING of <see cref="RoleProcessSupervisor"/>,
/// not a reuse: role children are keyed one-per-role by <see cref="ShellRole"/>, but the user opens
/// N Filer windows, so this supervisor keys dynamic <em>instances</em> and spawns on demand. Policy
/// mirrors the role supervisor where the domains agree — single crash-monitor loop, per-instance
/// poll-tick backoff, one gate serializing open/teardown/restart against the monitor — and diverges
/// where the bead rules:
/// <list type="bullet">
/// <item><b>Close vs. crash.</b> A child that exits with code 0 closed its own window — the user hid
/// the surface, so it is de-supervised and NEVER respawned (<c>bevel-gdie</c>'s user-hidden rule).
/// A child that dies any other way (code != 0, or no readable code) had its window up: it is
/// respawned at its <see cref="FilerSpawnRequest"/> (last-open-path) with backoff.</item>
/// <item><b>Liveness is supervisor-only.</b> Filers write NO heartbeats — N instances would clobber
/// the single <c>ShellRole.Filer</c> slot in <c>RoleHeartbeatStore</c> (phantom-gap the health
/// monitor), so this supervisor never wires a <see cref="ShellHealthMonitor"/> and filer churn can
/// never Hold the shell. The role-keyed store stays for core/taskbar/desktop only.</item>
/// <item><b>Stderr is captured.</b> A dying peer's managed stack exists only in its stderr
/// (<c>bevel-peer-process-crashes</c>); each child's stderr lands in a per-pid file next to the
/// restart diag log, so a filer crash is diagnosable without luck.</item>
/// </list>
/// Pure policy: process mechanics live behind <see cref="IFilerProcess"/> and the poll delay is
/// injected, so tests drive the loop deterministically.
/// </summary>
internal sealed class FilerSupervisor : IAsyncDisposable
{
    /// <summary>One supervised Filer window: the request it opened with (and respawns with), the
    /// process currently serving it, and its crash-backoff state. The instance (and its Key) is
    /// stable across crash respawns — a restart of the SAME logical window — and fresh for every
    /// new open or RestartAll cycle.</summary>
    private sealed class Instance
    {
        public required FilerSpawnRequest Request { get; set; } // mutable: the park claim flips it to the open request
        public required long Key { get; init; }
        public required IFilerProcess Process { get; set; }
        // Per-instance crash backoff in POLL TICKS (clock-free, like RoleProcessSupervisor):
        // _cooldown is ticks still to wait before the next respawn attempt; _backoff doubles on
        // each consecutive failure and resets the first tick the child is seen alive again.
        public int Cooldown;
        public int Backoff;
    }

    private readonly IReadOnlyList<string> _launcherArgs;
    private readonly IReadOnlyDictionary<string, string> _childEnv;
    private readonly Func<FilerSpawnRequest, long, IFilerProcess> _factory;
    private readonly Func<int, string, bool, string?, CancellationToken, Task<bool>> _showSender;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;
    private readonly List<Instance> _instances = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // serializes open/teardown/restart vs the monitor
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private long _nextKey;
    private volatile bool _stopped;

    public FilerSupervisor(
        IReadOnlyList<string> launcherArgs,
        IReadOnlyDictionary<string, string> childEnv,
        Func<FilerSpawnRequest, long, IFilerProcess>? factory = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? maxBackoff = null,
        Action<string>? log = null,
        Func<int, string, bool, string?, CancellationToken, Task<bool>>? showSender = null)
    {
        _launcherArgs = launcherArgs;
        _childEnv = childEnv;
        _factory = factory ?? ProductionFactory;
        // The parked-handoff dial: (pid, path, search, select) → did the parked Filer accept the Show?
        // Injected in tests; in production a bounded FilerControlChannel dial to filer-<pid>.sock.
        _showSender = showSender ?? ProductionShowSender;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _delay = delay ?? Task.Delay;
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        _log = log;
    }

    /// <summary>Launches the crash-monitor loop. No children exist yet (parked pre-warm is Task 4);
    /// starting the loop here keeps it alive for the whole shell session like the role supervisor's.</summary>
    public void Start()
    {
        if (_stopped) return;
        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
    }

    /// <summary>Opens a Filer window as a supervised child. Ack: true once the spawn is accepted;
    /// false on launch failure (logged) so the caller's fallback — the taskbar's in-process
    /// spawner — still gets the user a window. Never throws to the control-server handler.</summary>
    public async Task<bool> OpenAsync(string openPath, bool search = false, string? selectPath = null,
        CancellationToken ct = default)
    {
        if (_stopped) return false;
        var request = new FilerSpawnRequest(openPath, search, selectPath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Instance? claimed = null;
        try
        {
            // Parked handoff (bevel-t48y Task 4): a live parked instance is CLAIMED under this lock —
            // its request flips to the open request — BEFORE the Show dial leaves the gate, so a second
            // rapid open can never claim (or double-show) the same instance. If the dial then fails,
            // the claim is reverted below and the park stays parked; the open falls back to a cold spawn.
            claimed = FindParkedLocked();
            if (claimed is not null)
            {
                claimed.Request = request; // last-open-path tracking: the claim IS the open
                _log?.Invoke($"filer supervisor: handing parked #{claimed.Key} to {openPath}");
            }
            else
            {
                return ColdSpawnLocked(request);
            }
        }
        finally { _gate.Release(); }

        // Outside the gate: the dial is bounded but must not hold other opens hostage.
        bool shown;
        try
        {
            shown = await _showSender(claimed!.Process.Pid, openPath, search, selectPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"filer supervisor: Show dial to #{claimed.Key} faulted ({ex.Message}) — cold-spawning instead");
            shown = false;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (shown)
            {
                _log?.Invoke($"filer supervisor: parked #{claimed.Key} shown at {openPath}");
                return ParkLockedAsync(); // keep-1: replacement park
            }
            // Failed handoff: un-claim. The park instance stays supervised as a park (if it died,
            // the monitor re-parks it via the crash policy — keep-1 holds either way).
            claimed.Request = new FilerSpawnRequest(null, false, null, Park: true);
            _log?.Invoke($"filer supervisor: Show to #{claimed.Key} failed — park retained, cold-spawning");
            return ColdSpawnLocked(request);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Spawns one parked pre-warmed Filer (Task 4): a hidden <c>--park</c> child whose window
    /// is already built, so an open is a Show dial — a frame, not a process cold start. One park at a
    /// time (keep-1); the open handoff or the crash monitor spawns the replacement.</summary>
    public async Task<bool> ParkAsync(CancellationToken ct = default)
    {
        if (_stopped) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return ParkLockedAsync(); }
        finally { _gate.Release(); }
    }

    private bool ParkLockedAsync()
    {
        if (_stopped) return false;
        if (FindParkedLocked() is not null) return true; // keep-1: one park, never two
        var instance = NewInstance(new FilerSpawnRequest(null, false, null, Park: true));
        try { instance.Process.Start(); }
        catch (Exception ex)
        {
            instance.Process.Dispose();
            _log?.Invoke($"filer supervisor: park spawn failed: {ex.Message}");
            return false;
        }
        lock (_instances) _instances.Add(instance);
        _log?.Invoke($"filer supervisor: parked #{instance.Key}");
        return true;
    }

    private Instance? FindParkedLocked() =>
        _instances.FirstOrDefault(i => i.Request.Park && i.Process.IsAlive);

    private bool ColdSpawnLocked(FilerSpawnRequest request)
    {
        var instance = NewInstance(request);
        try
        {
            instance.Process.Start();
        }
        catch (Exception ex)
        {
            instance.Process.Dispose();
            // Visible failure (bevel-t48y): the old path's one Console.Error line to a
            // detached shell was the invisible-failure class this supervisor exists to delete.
            _log?.Invoke($"filer supervisor: spawn failed for {request.OpenPath}: {ex.Message}");
            return false;
        }
        lock (_instances) _instances.Add(instance);
        _log?.Invoke($"filer supervisor: opened #{instance.Key} at {request.OpenPath}");
        return true;
    }

    /// <summary>Kills and respawns every LIVE filer at its last-open-path — each comes back on the
    /// CURRENT binary (RestartAll is the self-update path). Fresh instance keys: a restart is a new
    /// process. Instances whose window the user closed are long gone and stay gone.</summary>
    public async Task RestartAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log?.Invoke("filer supervisor: restarting all filers");
            foreach (var instance in _instances.ToList())
            {
                instance.Process.Kill();
                instance.Process.Dispose();
                var fresh = NewInstance(instance.Request); // last-open-path, clean backoff
                try { fresh.Process.Start(); }
                catch (Exception ex)
                {
                    // Leave it supervised with backoff so the monitor retries — don't drop a live
                    // window's slot just because the respawn raced a binary swap.
                    fresh.Process.Dispose();
                    fresh.Cooldown = fresh.Backoff = 1;
                    _log?.Invoke($"filer supervisor: restart of {instance.Request.OpenPath} failed ({ex.Message}); monitor will retry");
                }
                lock (_instances)
                {
                    _instances.Remove(instance);
                    _instances.Add(fresh);
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Latches the stopping state and halts respawns IMMEDIATELY — safe to call
    /// synchronously from a signal handler, mirroring <see cref="RoleProcessSupervisor.RequestStop"/>
    /// (bevel-ply: a child exiting on its own SIGTERM must not be read as a crash).</summary>
    public void RequestStop()
    {
        _stopped = true;
        _monitorCts?.Cancel();
    }

    /// <summary>Stops the monitor and kills every filer child — Quit teardown. Idempotent, and the
    /// latch guarantees no child exited by this teardown is ever respawned.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        RequestStop();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        await KillAllLockedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The single crash-monitor loop. Each tick judges every instance under the gate:
    /// alive → backoff reset; dead with exit 0 → user close, de-supervise forever; dead any other
    /// way → crash, respawn at last-open-path with per-instance backoff. An escaping exception
    /// would permanently disable supervision, so the per-tick body is guarded.</summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var maxCooldown = Math.Max(1, (int)(_maxBackoff.Ticks / Math.Max(1, _pollInterval.Ticks)));
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelled(_pollInterval, ct).ConfigureAwait(false)) break;
            if (_stopped) break;

            if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
                continue; // an open/restart/teardown holds the gate — skip this tick, it owns the set
            try
            {
                foreach (var instance in Snapshot())
                {
                    if (ct.IsCancellationRequested || _stopped) break;
                    var process = instance.Process;

                    if (process.IsAlive)
                    {
                        // Seen alive a full tick — this is the SOLE backoff reset (an instant
                        // crash-loop never resets it, mirroring RoleProcessSupervisor's escalation).
                        instance.Cooldown = instance.Backoff = 0;
                        continue;
                    }

                    // WE are tearing down — never judge (let alone respawn) a child this process killed.
                    if (_stopped) break;

                    var code = process.ExitCode;
                    if (code == 0 && !instance.Request.Park)
                    {
                        lock (_instances) _instances.Remove(instance);
                        process.Dispose();
                        _log?.Invoke($"filer supervisor: #{instance.Key} closed by user at {instance.Request.OpenPath} — not respawning (bevel-gdie)");
                        continue;
                    }

                    if (instance.Cooldown > 0)
                    {
                        instance.Cooldown--; // serve the backoff: a crash-looping binary must not spin hot
                        continue;
                    }

                    process.Dispose();
                    instance.Backoff = instance.Backoff <= 0 ? 1 : Math.Min(instance.Backoff * 2, maxCooldown);
                    instance.Cooldown = instance.Backoff;
                    try
                    {
                        _log?.Invoke($"filer supervisor: #{instance.Key} died (exit={code?.ToString() ?? "signal"}) — respawning at {instance.Request.OpenPath}");
                        instance.Process = _factory(instance.Request, instance.Key);
                        instance.Process.Start();
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"filer supervisor: #{instance.Key} respawn failed ({ex.Message}); backing off {instance.Backoff} tick(s)");
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }

    private Instance NewInstance(FilerSpawnRequest request)
    {
        var key = Interlocked.Increment(ref _nextKey);
        return new Instance { Request = request, Key = key, Process = _factory(request, key) };
    }

    private async Task KillAllLockedAsync(CancellationToken ct)
    {
        // Bound the gate acquire: a wedged monitor tick must not block launcher teardown forever
        // (the Kill() calls are independently safe — bevel-ply, same reasoning as StopAsync there).
        var haveGate = false;
        try { haveGate = await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try
        {
            lock (_instances)
            {
                foreach (var instance in _instances)
                {
                    instance.Process.Kill();
                    instance.Process.Dispose();
                }
                _instances.Clear();
            }
        }
        finally { if (haveGate) _gate.Release(); }
    }

    private IFilerProcess ProductionFactory(FilerSpawnRequest request, long key)
    {
        // The supervised spawn path composes children with the SAME builder as core/taskbar/desktop
        // (Task 1's generalized CreateRoleStartInfo) — a DIRECT launcher child for TCC attribution,
        // never /bin/sh/nohup-detached (the attribution breaker, Review Focus #1).
        List<string> extra;
        if (request.Park)
        {
            extra = new List<string> { "--park" };
        }
        else
        {
            extra = new List<string> { "--open-path=" + request.OpenPath };
            if (request.Search) extra.Add("--search");
            if (!string.IsNullOrEmpty(request.SelectPath)) extra.Add("--select=" + request.SelectPath);
        }
        var startInfo = Program.CreateRoleStartInfo(ShellRole.Filer, _launcherArgs, _childEnv, extra);
        return new FilerProcess(startInfo);
    }

    /// <summary>The parked-handoff dial: a bounded FilerControlChannel round-trip to the claimed
    /// park's <c>filer-&lt;pid&gt;.sock</c>. The taskbar's dialer is reused — it is just a client of
    /// the shared rendezvous, and the launcher published the same dir+nonce at boot.</summary>
    private static async Task<bool> ProductionShowSender(int pid, string openPath, bool search,
        string? selectPath, CancellationToken ct)
    {
        var client = new TaskbarFilerControlClient(FilerControlEndpoint.Dir, FilerControlEndpoint.ResolveNonce());
        return await client.ShowByPidAsync(pid, openPath, search, selectPath, ct).ConfigureAwait(false);
    }

    /// <summary>Test seam: the processes currently supervised (one per live instance), so tests can
    /// flip a fake's liveness/exit code to drive the crash/close policy.</summary>
    internal IReadOnlyList<IFilerProcess> LiveProcessesForTests() => Snapshot().Select(i => i.Process).ToList();

    private List<Instance> Snapshot()
    {
        lock (_instances) return _instances.ToList();
    }

    private async Task<bool> DelayOrCancelled(TimeSpan delay, CancellationToken ct)
    {
        try { await _delay(delay, ct).ConfigureAwait(false); return false; }
        catch (OperationCanceledException) { return true; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
        _monitorCts?.Dispose();
    }
}
