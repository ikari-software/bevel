namespace Bevel.Taskbar.Components;

/// <summary>What to do with a component after a crash or a watchdog check.</summary>
public enum ComponentVerdict { Healthy, Retry, Quarantine }

/// <summary>
/// Per-component crash and liveness budget, deliberately SEPARATE from ShellHealthMonitor's role
/// budget: a looping component must never trip the shell's CrashLoop hold, because that hold stops
/// respawning the taskbar and in Windows shell mode leaves the user with no shell.
///
/// Quarantine beats infinite respawn — an exhausted component's slot renders inert with a "component
/// failed" affordance until the user re-enables it or the bar restarts.
/// </summary>
public sealed class ComponentHealth
{
    private readonly int _crashBudget;
    private readonly int _watchdogMs;
    private readonly Dictionary<string, int> _crashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _heartbeats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    public ComponentHealth(int crashBudget = 3, int watchdogMs = 5000)
    {
        _crashBudget = Math.Max(1, crashBudget);
        _watchdogMs = Math.Max(100, watchdogMs);
    }

    /// <summary>Records a crash and says whether to retry or quarantine. Idempotent once quarantined.</summary>
    public ComponentVerdict RecordCrash(string instanceId)
    {
        if (_quarantined.Contains(instanceId)) return ComponentVerdict.Quarantine;

        var n = _crashes.TryGetValue(instanceId, out var prior) ? prior + 1 : 1;
        _crashes[instanceId] = n;

        if (n < _crashBudget) return ComponentVerdict.Retry;
        _quarantined.Add(instanceId);
        return ComponentVerdict.Quarantine;
    }

    /// <summary>Notes that the component is alive, at a caller-supplied instant.</summary>
    public void RecordHeartbeat(string instanceId, DateTime at) => _heartbeats[instanceId] = at;

    /// <summary>Notes that the component is alive now. Production convenience over the testable overload.</summary>
    public void RecordHeartbeat(string instanceId) => RecordHeartbeat(instanceId, DateTime.UtcNow);

    /// <summary>
    /// A component that stopped answering is not crashed, so nothing else would notice. Past the
    /// deadline it is quarantined and its slot goes inert rather than silently freezing.
    /// </summary>
    public ComponentVerdict CheckWatchdog(string instanceId, DateTime now)
    {
        if (_quarantined.Contains(instanceId)) return ComponentVerdict.Quarantine;
        if (!_heartbeats.TryGetValue(instanceId, out var last)) return ComponentVerdict.Healthy;

        if ((now - last).TotalMilliseconds <= _watchdogMs) return ComponentVerdict.Healthy;
        _quarantined.Add(instanceId);
        return ComponentVerdict.Quarantine;
    }

    /// <summary>Clears crash count and quarantine, for an explicit user re-enable or a bar restart.</summary>
    public void Reset(string instanceId)
    {
        _crashes.Remove(instanceId);
        _heartbeats.Remove(instanceId);
        _quarantined.Remove(instanceId);
    }
}
