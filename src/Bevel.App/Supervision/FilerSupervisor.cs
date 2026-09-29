using System.Collections.Concurrent;

namespace Bevel.App.Supervision;

/// <summary>What a supervised Filer is opened with — and what it is RESPAWNED with (crash policy:
/// restart at last-open-path, never with window state). Immutable per logical instance.</summary>
internal sealed record FilerSpawnRequest(string OpenPath, bool Search, string? SelectPath);

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
        public required FilerSpawnRequest Request { get; init; }
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
        Action<string>? log = null)
    {
        _launcherArgs = launcherArgs;
        _childEnv = childEnv;
        _factory = factory ?? ProductionFactory;
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
        try
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
                _log?.Invoke($"filer supervisor: spawn failed for {openPath}: {ex.Message}");
                return false;
            }
            lock (_instances) _instances.Add(instance);
            _log?.Invoke($"filer supervisor: opened #{instance.Key} at {openPath}");
            return true;
        }
        finally { _gate.Release(); }
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
                    if (code == 0)
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
        var extra = new List<string> { "--open-path=" + request.OpenPath };
        if (request.Search) extra.Add("--search");
        if (!string.IsNullOrEmpty(request.SelectPath)) extra.Add("--select=" + request.SelectPath);
        var startInfo = Program.CreateRoleStartInfo(ShellRole.Filer, _launcherArgs, _childEnv, extra);
        return new FilerProcess(startInfo);
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
