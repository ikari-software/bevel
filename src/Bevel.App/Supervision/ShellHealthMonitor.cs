namespace Bevel.App.Supervision;

internal enum HealthFault
{
    None,
    StartupFailed,
    StartupStuck,
    VersionSkew,
    Communication,
    CrashLoop,
}

internal enum HealthAction
{
    Continue,
    Restart,
    Hold,
}

internal readonly record struct HealthVerdict(
    ShellRole Target,
    HealthAction Action,
    HealthFault Fault,
    string Detail)
{
    public static HealthVerdict Ok(ShellRole target) =>
        new(target, HealthAction.Continue, HealthFault.None, "");
}

/// <summary>
/// Clock-free health policy for one supervised role (bevel-9h7n). The supervisor feeds it
/// (alive, latest heartbeat) once per poll tick and honours the verdict: Continue (existing
/// crash-respawn path), Restart a live-but-wrong child, or Hold (stop respawning + alert).
///
/// Tick counts, not wall clocks, so tests stay deterministic like the rest of
/// <see cref="RoleProcessSupervisor"/>.
/// </summary>
internal sealed class ShellHealthMonitor
{
    private readonly Func<string> _expectedStamp;
    private readonly int _readyTimeoutTicks;
    private readonly int _crashLoopLimit;
    private readonly int _skewRestartBudget;
    private readonly int _commTimeoutTicks;
    private readonly Dictionary<ShellRole, State> _states = new();

    public ShellHealthMonitor(
        Func<string> expectedStamp,
        int readyTimeoutTicks = 15,
        int crashLoopLimit = 3,
        int skewRestartBudget = 2,
        int commTimeoutTicks = 8)
    {
        _expectedStamp = expectedStamp;
        _readyTimeoutTicks = Math.Max(1, readyTimeoutTicks);
        _crashLoopLimit = Math.Max(1, crashLoopLimit);
        _skewRestartBudget = Math.Max(1, skewRestartBudget);
        _commTimeoutTicks = Math.Max(1, commTimeoutTicks);
    }

    public HealthVerdict Observe(ShellRole role, bool alive, RoleHeartbeat? hb)
    {
        var s = Get(role);
        if (s.Held)
            return new HealthVerdict(role, HealthAction.Hold, s.HeldFault, s.HeldDetail);

        if (!alive)
            return ObserveDead(role, s, hb);

        s.Dead = false;

        if (hb is { Status: HeartbeatStatus.Failed })
            return Hold(s, role, HealthFault.StartupFailed, NonEmpty(hb.Value.Error, $"{role} reported startup failure"));

        if (hb is { Stale: true, Status: HeartbeatStatus.Ready })
            return new HealthVerdict(role, HealthAction.Restart, HealthFault.Communication,
                $"{role} heartbeat went stale while the process is still alive");

        if (IsSkew(hb, out var skewDetail))
        {
            s.SkewRestarts++;
            if (s.SkewRestarts > _skewRestartBudget)
                return Hold(s, role, HealthFault.VersionSkew, $"{skewDetail} after {s.SkewRestarts} restarts");
            return new HealthVerdict(role, HealthAction.Restart, HealthFault.VersionSkew, skewDetail);
        }

        if (hb is { Status: HeartbeatStatus.Ready })
        {
            s.EverReady = true;
            s.TicksWithoutReady = 0;
            s.DeathsNeverReady = 0;
            s.StuckRestarts = 0;

            if (role is ShellRole.Taskbar or ShellRole.Desktop or ShellRole.Explorer
                && !hb.Value.CoreConnected)
            {
                s.CommTicks++;
                if (s.CommTicks >= _commTimeoutTicks)
                {
                    s.CommTicks = 0; // give the respawned core a full grace window
                    return new HealthVerdict(ShellRole.Core, HealthAction.Restart, HealthFault.Communication,
                        $"{role} is ready but has no core link for {_commTimeoutTicks} ticks");
                }
            }
            else
            {
                s.CommTicks = 0;
            }

            return HealthVerdict.Ok(role);
        }

        s.TicksWithoutReady++;
        if (s.TicksWithoutReady >= _readyTimeoutTicks)
        {
            s.TicksWithoutReady = 0;
            s.StuckRestarts++;
            if (s.StuckRestarts >= _crashLoopLimit)
                return Hold(s, role, HealthFault.StartupStuck,
                    $"{role} never became ready after {s.StuckRestarts} restarts");
            return new HealthVerdict(role, HealthAction.Restart, HealthFault.StartupStuck,
                $"{role} still starting after {_readyTimeoutTicks} ticks");
        }

        return HealthVerdict.Ok(role);
    }

    public bool IsHeld(ShellRole role) => _states.TryGetValue(role, out var s) && s.Held;

    /// <summary>A monitor-loop respawn of a child that has never been ready. Fast crashers
    /// (XAML throw in Main) die before the next tick ever sees them alive, so death-edge
    /// counting would never trip; spawn counting does.</summary>
    public void NoteRespawn(ShellRole role)
    {
        var s = Get(role);
        if (s.Held || s.EverReady) return;
        s.Respawns++;
    }

    /// <summary>Drop hold/counters. RestartAll and an explicit SpawnRole get a fresh chance.</summary>
    public void Reset(ShellRole? role = null)
    {
        if (role is { } r) _states.Remove(r);
        else _states.Clear();
    }

    private HealthVerdict ObserveDead(ShellRole role, State s, RoleHeartbeat? hb)
    {
        if (!s.Dead)
        {
            s.Dead = true;
            if (!s.EverReady) s.DeathsNeverReady++;
        }

        if (hb is { Status: HeartbeatStatus.Failed })
            return Hold(s, role, HealthFault.StartupFailed, NonEmpty(hb.Value.Error, $"{role} reported startup failure"));

        if (!s.EverReady && s.Respawns >= _crashLoopLimit)
            return Hold(s, role, HealthFault.CrashLoop,
                $"{role} died {s.Respawns} times before becoming ready");

        return HealthVerdict.Ok(role);
    }

    private bool IsSkew(RoleHeartbeat? hb, out string detail)
    {
        detail = "";
        if (hb is null) return false;
        var snap = hb.Value;
        if (string.IsNullOrEmpty(snap.Stamp)) return false;
        var expected = _expectedStamp() ?? "";
        if (string.IsNullOrEmpty(expected)) return false;
        if (snap.Proto != 0 && snap.Proto != BuildStamp.Protocol)
        {
            detail = $"{snap.Role} protocol {snap.Proto} != {BuildStamp.Protocol}";
            return true;
        }
        if (!string.Equals(snap.Stamp, expected, StringComparison.Ordinal))
        {
            detail = $"{snap.Role} stamp '{snap.Stamp}' != launcher '{expected}'";
            return true;
        }
        return false;
    }

    private static HealthVerdict Hold(State s, ShellRole role, HealthFault fault, string detail)
    {
        s.Held = true;
        s.HeldFault = fault;
        s.HeldDetail = detail;
        return new HealthVerdict(role, HealthAction.Hold, fault, detail);
    }

    private State Get(ShellRole role)
    {
        if (!_states.TryGetValue(role, out var s))
            _states[role] = s = new State();
        return s;
    }

    private static string NonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed class State
    {
        public int TicksWithoutReady;
        public int DeathsNeverReady;
        public int Respawns;
        public int SkewRestarts;
        public int StuckRestarts;
        public int CommTicks;
        public bool EverReady;
        public bool Dead;
        public bool Held;
        public HealthFault HeldFault;
        public string HeldDetail = "";
    }
}
