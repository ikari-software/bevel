namespace Bevel.App.Supervision;

/// <summary>
/// Supervises the multi-process shell (bevel-gww.4). It starts the role processes in dependency order
/// — the shell-core owner (which brings up the Swift helper) BEFORE the UI roles that are its clients —
/// then runs a single crash-monitor loop that respawns any child that dies, with per-child backoff so a
/// crash-looping binary can't spin hot. When a <see cref="ShellHealthMonitor"/> is wired (bevel-9h7n),
/// heartbeats additionally drive startup-fail / version-skew / lost-IPC heals, and a Hold stops the
/// silent crash-loop and surfaces a user alert. Two fan-out operations back the goal's headline:
/// <list type="bullet">
/// <item><see cref="RestartAllAsync"/> — kill every process and respawn it; each comes back on the
/// CURRENT binary, so a taskbar-initiated restart updates the whole shell to the latest build.</item>
/// <item><see cref="RestartCoreAsync"/> — swap just the shell-core owner while the UI stays up
/// ("core easy to update on demand").</item>
/// </list>
///
/// <para><b>Deadlock avoidance (arch-spec).</b> Every lock here is launcher-local (<see cref="_gate"/>)
/// and process kills are strictly parent→child: no child ever blocks the launcher, so restarting the
/// core can never wedge on a UI process that is itself waiting on the core.</para>
///
/// <para>Pure policy: process mechanics live behind <see cref="IRoleProcess"/> and the poll delay +
/// core-readiness probe are injected, so the loop is driven deterministically in tests.</para>
/// </summary>
internal sealed class RoleProcessSupervisor : IAsyncDisposable
{
    // Dependency order: core first, UIs after. MUTABLE so a surface can be added/removed at runtime —
    // the Start-menu "Show/Hide Desktop" toggle (bevel-gdie) appends/removes the desktop child. Every
    // read AND every mutation happens under _gate, so the monitor loop can never see it mid-edit.
    private readonly List<IRoleProcess> _processes;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;
    private readonly Func<CancellationToken, Task>? _coreReadyProbe;
    private readonly Func<CancellationToken, Task<bool>>? _coreHealthyProbe;
    private readonly Func<bool>? _quitRequested;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;
    private readonly ShellHealthMonitor? _health;
    private readonly Func<ShellRole, RoleHeartbeat?>? _heartbeat;
    private readonly Action<HealthVerdict>? _onAlert;
    // Whole-shell relaunch on launcher skew (bevel-t48y follow-up / the "2-3 relaunches" report):
    // fires ONCE when a VersionSkew verdict is observed. Null = the legacy heal (restart children,
    // skew again, Hold after the budget) — kept for tests and unsupervised callers.
    private readonly Action? _onLauncherStale;
    private bool _launcherStaleFired;
    // One native/status alert per role per hold episode — Observe returns Hold every later
    // tick, and popping a dialog per second would be the new invisible-failure mode.
    private readonly HashSet<ShellRole> _alerted = new();
    // Consecutive failed health probes on a still-running core (bevel-hprv). Reset when the
    // socket looks healthy again; at the threshold we treat the process as dead and respawn.
    private int _coreUnhealthyTicks;

    // Serializes the start / restart / stop operations against the monitor loop's respawns, so a
    // RestartAll can never race a per-child crash-respawn into a double-spawn.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Per-child crash backoff in POLL TICKS (clock-free, so tests stay deterministic): _cooldown is
    // ticks still to wait before the next respawn attempt; _backoffTicks is the current backoff length,
    // doubled on each consecutive failure and capped at _maxBackoff / _pollInterval.
    private readonly Dictionary<ShellRole, int> _cooldown = new();
    private readonly Dictionary<ShellRole, int> _backoffTicks = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private volatile bool _stopped;

    public RoleProcessSupervisor(
        IReadOnlyList<IRoleProcess> processes,
        TimeSpan pollInterval,
        Func<CancellationToken, Task>? coreReadyProbe = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? maxBackoff = null,
        Action<string>? log = null,
        Func<CancellationToken, Task<bool>>? coreHealthyProbe = null,
        Func<bool>? quitRequested = null,
        ShellHealthMonitor? health = null,
        Func<ShellRole, RoleHeartbeat?>? heartbeat = null,
        Action<HealthVerdict>? onAlert = null,
        Action? onLauncherStale = null)
    {
        if (processes.Count == 0) throw new ArgumentException("Supervise at least one process.", nameof(processes));
        _processes = processes.ToList(); // own a private, mutable copy — runtime add/remove edits this list, not the caller's
        _pollInterval = pollInterval;
        _coreReadyProbe = coreReadyProbe;
        _coreHealthyProbe = coreHealthyProbe;
        _quitRequested = quitRequested;
        _delay = delay ?? Task.Delay;
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        _log = log;
        _health = health;
        _heartbeat = heartbeat ?? (health is null ? null : RoleHeartbeatStore.Read);
        _onAlert = onAlert;
        _onLauncherStale = onLauncherStale;
    }

    /// <summary>Starts every process in order (core → UIs), then launches the crash-monitor loop.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartAllInOrderLocked(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
    }

    /// <summary>Kills and respawns every process — each returns on the CURRENT binary. This is what a
    /// taskbar restart triggers, so the whole shell updates to the latest build in one action.</summary>
    public async Task RestartAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log?.Invoke("supervisor: restarting all role processes");
            // Tear down UIs before the core (reverse dependency order) so no UI briefly talks to a
            // dying core; then start back up core-first.
            for (var i = _processes.Count - 1; i >= 0; i--)
                _processes[i].Kill();
            _cooldown.Clear();
            _backoffTicks.Clear();
            _alerted.Clear();
            _health?.Reset();
            if (_health is not null) RoleHeartbeatStore.ClearAll();
            await StartAllInOrderLocked(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Swaps just the shell-core owner (update-on-demand); the UI processes stay running and
    /// their shell-core clients reconnect on next use.</summary>
    public async Task RestartCoreAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var core = _processes.FirstOrDefault(p => p.Role == ShellRole.Core);
            if (core is null) { _log?.Invoke("supervisor: no core process to restart"); return; }
            _log?.Invoke("supervisor: restarting shell-core owner");
            core.Kill();
            _health?.Reset(ShellRole.Core);
            _alerted.Remove(ShellRole.Core);
            if (_health is not null) RoleHeartbeatStore.Clear(ShellRole.Core);
            core.Start();
            _cooldown.Remove(ShellRole.Core);
            _backoffTicks.Remove(ShellRole.Core);
            if (_coreReadyProbe is not null)
                await _coreReadyProbe(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Adds a role to the supervised set at runtime and starts it (bevel-gdie: Start ▸ "Show Desktop").
    /// Idempotent — a second request while the role is already supervised is a no-op, so a double-click
    /// can't spawn two. The child is APPENDED (after core+taskbar), so the crash-monitor picks it up and
    /// keeps it alive like any other; z-order is owned by the window's own level, not spawn order. If the
    /// initial <see cref="IRoleProcess.Start"/> throws it stays in the set so the monitor retries it with
    /// backoff (same policy as a crashed core), rather than silently dropping a requested surface.
    /// </summary>
    public async Task SpawnRoleAsync(ShellRole role, Func<IRoleProcess> factory, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_processes.Any(p => p.Role == role))
            {
                _log?.Invoke($"supervisor: {role} already supervised — spawn request ignored");
                return;
            }
            _log?.Invoke($"supervisor: spawning {role} on demand");
            var proc = factory();
            _processes.Add(proc);
            _cooldown.Remove(role);
            _backoffTicks.Remove(role);
            _alerted.Remove(role);
            _health?.Reset(role);
            if (_health is not null) RoleHeartbeatStore.Clear(role);
            try { proc.Start(); }
            catch (Exception ex)
            {
                // Leave it supervised so the monitor retries with backoff — don't drop a requested surface.
                _log?.Invoke($"supervisor: {role} initial start failed ({ex.Message}); monitor will retry");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Removes a role from the supervised set and terminates it (bevel-gdie: Start ▸ "Hide Desktop").
    /// The list entry is dropped BEFORE the kill so the 1s monitor tick can't respawn it in the window
    /// between kill and edit (the same race the _stopped latch guards for whole-shell teardown) — a
    /// user-hidden surface must stay hidden, never auto-restart. No-op when the role isn't supervised.
    /// </summary>
    public async Task CloseRoleAsync(ShellRole role, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var proc = _processes.FirstOrDefault(p => p.Role == role);
            if (proc is null) { _log?.Invoke($"supervisor: no {role} process to close"); return; }
            _log?.Invoke($"supervisor: closing {role} on demand");
            _processes.Remove(proc);      // de-supervise FIRST so the monitor can't respawn it mid-kill
            _cooldown.Remove(role);
            _backoffTicks.Remove(role);
            _alerted.Remove(role);
            _health?.Reset(role);
            proc.Kill();
            proc.Dispose();
            if (_health is not null) RoleHeartbeatStore.Clear(role);
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when a live process for <paramref name="role"/> is currently supervised. Taken under
    /// _gate so it never enumerates the list mid-edit (a concurrent Spawn/Close mutates it): the toggle's
    /// Show/Hide label reads this. A stale-by-a-moment answer is harmless (the label recomputes on next
    /// open); a torn enumeration would not be, hence the gate.</summary>
    public async Task<bool> IsRoleRunningAsync(ShellRole role, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _processes.Any(p => p.Role == role && p.IsAlive); }
        finally { _gate.Release(); }
    }

    /// <summary>Stops the monitor loop and kills every process (reverse order). Idempotent.</summary>
    /// <summary>Latches the supervisor into the stopping state and halts child respawns IMMEDIATELY —
    /// safe to call synchronously from a signal handler, before the async <see cref="StopAsync"/>
    /// teardown runs. Without this, a `kill -TERM` delivered to the launcher AND its children races the
    /// monitor loop: a child that exits cleanly on its own SIGTERM looks "died" to the 1s monitor tick,
    /// which respawns it (new PID) because _stopped isn't set yet — the shell appears to "not die" and
    /// needs SIGKILL. Setting the latch on signal receipt closes that window (bevel-ply).</summary>
    public void RequestStop()
    {
        _stopped = true;
        _monitorCts?.Cancel();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _stopped = true;
        _monitorCts?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }

        // Bound the gate acquire: a wedged monitor tick must not block launcher teardown forever
        // (the 8s cap above only bounds waiting for the task, not the gate). The child Kill() calls
        // are independently safe, so proceed to kill even if the gate didn't free in time (bevel-ply).
        var haveGate = false;
        try { haveGate = await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try
        {
            for (var i = _processes.Count - 1; i >= 0; i--)
                _processes[i].Kill();
        }
        finally { if (haveGate) _gate.Release(); }
    }

    private async Task StartAllInOrderLocked(CancellationToken ct)
    {
        // Start every role back-to-back (dependency order preserved) so their cold starts OVERLAP
        // (bevel-2cbo): the taskbar no longer waits for the core to be reachable before it even forks.
        // Its shell-core client already retries the connect (ShellModel: 40×750ms + the 2s reconcile),
        // so overlapping the two cold starts removes the core's entire startup from time-to-taskbar-
        // visible. Core readiness is still awaited AFTER spawning, so callers can assume a reachable
        // core — but the taskbar has already forked in parallel by then.
        var started = new List<IRoleProcess>();
        try
        {
            foreach (var p in _processes)
            {
                _log?.Invoke($"supervisor: starting {p.Role}");
                p.Start();
                started.Add(p);
            }
        }
        catch
        {
            // A mid-fan-out Start() failure must not orphan the children already spawned — kill
            // them in reverse before propagating (mirrors StopAsync's teardown; ce-review: reliability).
            for (var i = started.Count - 1; i >= 0; i--)
                try { started[i].Kill(); } catch { /* best-effort */ }
            throw;
        }
        if (_coreReadyProbe is not null)
            await _coreReadyProbe(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single crash-monitor loop (started once). Each tick respawns any dead child under the same
    /// gate the restart operations use, with per-child exponential backoff that resets once the child
    /// is healthy again. An escaping exception would permanently disable supervision, so the per-tick
    /// body is guarded.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelled(_pollInterval, ct).ConfigureAwait(false)) break;
            if (_stopped) break;

            if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
                continue; // a restart/stop holds the gate — skip this tick, it owns the processes
            try
            {
                // Quit intent (bevel-0md2) wins over crash-respawn: a child that exited because the
                // user asked to quit must not come back, even if RequestStop raced this tick.
                if (_quitRequested?.Invoke() == true)
                {
                    RequestStop();
                    break;
                }

                var maxCooldown = Math.Max(1, (int)(_maxBackoff.Ticks / Math.Max(1, _pollInterval.Ticks)));
                foreach (var p in _processes)
                {
                    if (ct.IsCancellationRequested) break;

                    var alive = p.IsAlive;
                    if (alive && p.Role == ShellRole.Core && !await CoreLooksHealthyAsync(ct).ConfigureAwait(false))
                    {
                        _log?.Invoke("supervisor: core process is alive but its socket is gone — restarting");
                        try { p.Kill(); } catch { /* already gone */ }
                        if (_health is not null) RoleHeartbeatStore.Clear(p.Role);
                        alive = false;
                    }

                    if (_health is not null)
                    {
                        var verdict = ApplyHealth(p, ref alive);
                        if (verdict.Action == HealthAction.Hold) continue;
                    }

                    if (alive)
                    {
                        _cooldown.Remove(p.Role);
                        _backoffTicks.Remove(p.Role);
                        continue;
                    }

                    if (_health?.IsHeld(p.Role) == true) continue;

                    // Serve the backoff: skip this tick if the child is still cooling down from a
                    // prior failed respawn (a crash-looping binary must not spin hot).
                    if (_cooldown.TryGetValue(p.Role, out var wait) && wait > 0)
                    {
                        _cooldown[p.Role] = wait - 1;
                        continue;
                    }

                    try
                    {
                        // The stop latch can flip between the liveness check above and here (the cooldown
                        // check sits in that window). A child that exited on its OWN SIGTERM looks "died"
                        // to this tick; respawning it after the launcher's TERM fan-out gives it a fresh PID
                        // that never gets the signal — the shell "won't die" without SIGKILL (bevel-ply).
                        if (_stopped || ct.IsCancellationRequested) break;
                        _log?.Invoke($"supervisor: {p.Role} died — respawning");
                        // Escalate BEFORE spawning: a successful Process.Start() is NOT proof of
                        // health — a child that dies WITHIN the poll interval (bad config, crash on
                        // init) would otherwise reset its backoff here and respawn hot forever. A
                        // child still carrying a backoff entry died again without ever being seen
                        // alive, so it accumulates like a failed spawn; the top-of-loop IsAlive check
                        // (survived a full tick) is now the SOLE reset (ce-review: adversarial).
                        _backoffTicks[p.Role] = _backoffTicks.TryGetValue(p.Role, out var prev)
                            ? Math.Min(prev * 2, maxCooldown) : 1;
                        _cooldown[p.Role] = _backoffTicks[p.Role];
                        p.Start();
                        _health?.NoteRespawn(p.Role);
                        if (p.Role == ShellRole.Core)
                        {
                            _coreUnhealthyTicks = 0; // give the new process a full probe window
                            if (_coreReadyProbe is not null)
                                await _coreReadyProbe(ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _health?.NoteRespawn(p.Role);
                        var level = _backoffTicks.TryGetValue(p.Role, out var cur) ? Math.Min(cur * 2, maxCooldown) : 1;
                        _backoffTicks[p.Role] = level;
                        _cooldown[p.Role] = level;
                        _log?.Invoke($"supervisor: {p.Role} respawn failed ({ex.Message}); backing off {level} tick(s)");
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Startup / IPC / version-skew policy (bevel-9h7n). Hold stops the silent crash-loop;
    /// Restart kills a live-but-wrong child (or the core, when a ready UI has no link) so the
    /// existing dead-path respawns it. Alerts fire once per hold episode.
    /// </summary>
    private HealthVerdict ApplyHealth(IRoleProcess p, ref bool alive)
    {
        var hb = _heartbeat?.Invoke(p.Role);
        var verdict = _health!.Observe(p.Role, alive, hb);
        if (verdict.Fault != HealthFault.None)
            _log?.Invoke($"supervisor: health {verdict.Fault} {verdict.Action} {verdict.Target}: {verdict.Detail}");

        if (verdict.Action == HealthAction.Hold)
        {
            if (_alerted.Add(verdict.Target))
                _onAlert?.Invoke(verdict);
            if (verdict.Target == p.Role && alive)
            {
                try { p.Kill(); } catch { /* already gone */ }
                RoleHeartbeatStore.Clear(p.Role);
                alive = false;
            }
            return verdict;
        }

        // Launcher skew is un-healable by child restarts: a respawned child always comes back from
        // the CURRENT on-disk bundle, so a child-vs-launcher stamp mismatch can only mean the
        // RUNNING launcher predates the bundle (rebuild-while-running / version switch). The old
        // heal burned the skew budget restarting children that came back skewed every time, Held,
        // and waited for a manual relaunch — the "shell relaunches everything 2-3 times" symptom.
        // With a hook wired (the launcher), skew means: relaunch the WHOLE shell, one clean cycle.
        if (verdict.Fault == HealthFault.VersionSkew && _onLauncherStale is not null)
        {
            // With a hook wired, EVERY skew verdict is committed to the relaunch decision: fire the
            // hook once, and later ticks (a slow-quit launcher still observing skew) never fall
            // through to the legacy child-kill path.
            if (!_launcherStaleFired)
            {
                _launcherStaleFired = true;
                _log?.Invoke($"supervisor: {verdict.Fault} {verdict.Target} ({verdict.Detail}) — the running launcher predates the bundle on disk; relaunching the whole shell");
                _onLauncherStale();
            }
            return verdict; // no child kill/respawn: the relaunch teardown owns the children
        }

        if (verdict.Action == HealthAction.Restart)
        {
            if (verdict.Target != p.Role)
            {
                if (_health.IsHeld(verdict.Target)) return verdict;
                var other = _processes.FirstOrDefault(x => x.Role == verdict.Target);
                if (other is not null)
                {
                    _log?.Invoke($"supervisor: health restarting {other.Role} on behalf of {p.Role}");
                    try { other.Kill(); } catch { }
                    RoleHeartbeatStore.Clear(other.Role);
                }
            }
            else if (alive)
            {
                try { p.Kill(); } catch { }
                RoleHeartbeatStore.Clear(p.Role);
                alive = false;
            }
        }

        return verdict;
    }

    /// <summary>A live core whose socket is missing/unconnectable is as dead as a crashed one
    /// (bevel-hprv). Require a few consecutive failures so a core that is mid-rebind is not
    /// kill-looped. No probe configured → always healthy (tests / older callers).</summary>
    private async Task<bool> CoreLooksHealthyAsync(CancellationToken ct)
    {
        if (_coreHealthyProbe is null) return true;
        try
        {
            if (await _coreHealthyProbe(ct).ConfigureAwait(false))
            {
                _coreUnhealthyTicks = 0;
                return true;
            }
        }
        catch { /* a throwing probe is an unhealthy tick, not a monitor crash */ }

        _coreUnhealthyTicks++;
        return _coreUnhealthyTicks < 3; // still give it two more ticks
    }

    private async Task<bool> DelayOrCancelled(TimeSpan delay, CancellationToken ct)
    {
        try { await _delay(delay, ct).ConfigureAwait(false); return false; }
        catch (OperationCanceledException) { return true; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var p in _processes) p.Dispose();
        _gate.Dispose();
        _monitorCts?.Dispose();
    }
}
