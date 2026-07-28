namespace Bevel.App.Supervision;

/// <summary>
/// Supervises the multi-process shell (bevel-gww.4). It starts the role processes in dependency order
/// — the shell-core owner (which brings up the Swift helper) BEFORE the UI roles that are its clients —
/// then runs a single crash-monitor loop that respawns any child that dies, with per-child backoff so a
/// crash-looping binary can't spin hot. Two fan-out operations back the goal's headline:
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
    private readonly IReadOnlyList<IRoleProcess> _processes; // dependency order: core first, UIs after
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;
    private readonly Func<CancellationToken, Task>? _coreReadyProbe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

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
        Action<string>? log = null)
    {
        if (processes.Count == 0) throw new ArgumentException("Supervise at least one process.", nameof(processes));
        _processes = processes;
        _pollInterval = pollInterval;
        _coreReadyProbe = coreReadyProbe;
        _delay = delay ?? Task.Delay;
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        _log = log;
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
            core.Start();
            _cooldown.Remove(ShellRole.Core);
            _backoffTicks.Remove(ShellRole.Core);
            if (_coreReadyProbe is not null)
                await _coreReadyProbe(ct).ConfigureAwait(false);
        }
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = _processes.Count - 1; i >= 0; i--)
                _processes[i].Kill();
        }
        finally { _gate.Release(); }
    }

    private async Task StartAllInOrderLocked(CancellationToken ct)
    {
        // Start every role back-to-back (dependency order preserved) so their cold starts OVERLAP
        // (bevel-2cbo): the taskbar no longer waits for the core to be reachable before it even forks.
        // Its shell-core client already retries the connect (ShellModel: 40×750ms + the 2s reconcile),
        // so overlapping the two cold starts removes the core's entire startup from time-to-taskbar-
        // visible. Core readiness is still awaited AFTER spawning, so callers can assume a reachable
        // core — but the taskbar has already forked in parallel by then.
        foreach (var p in _processes)
        {
            _log?.Invoke($"supervisor: starting {p.Role}");
            p.Start();
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
                var maxCooldown = Math.Max(1, (int)(_maxBackoff.Ticks / Math.Max(1, _pollInterval.Ticks)));
                foreach (var p in _processes)
                {
                    if (ct.IsCancellationRequested || p.IsAlive)
                    {
                        if (p.IsAlive) { _cooldown.Remove(p.Role); _backoffTicks.Remove(p.Role); }
                        continue;
                    }

                    // Serve the backoff: skip this tick if the child is still cooling down from a
                    // prior failed respawn (a crash-looping binary must not spin hot).
                    if (_cooldown.TryGetValue(p.Role, out var wait) && wait > 0)
                    {
                        _cooldown[p.Role] = wait - 1;
                        continue;
                    }

                    try
                    {
                        _log?.Invoke($"supervisor: {p.Role} died — respawning");
                        p.Start();
                        if (p.Role == ShellRole.Core && _coreReadyProbe is not null)
                            await _coreReadyProbe(ct).ConfigureAwait(false);
                        _cooldown.Remove(p.Role);      // healthy again — reset backoff
                        _backoffTicks.Remove(p.Role);
                    }
                    catch (Exception ex)
                    {
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
