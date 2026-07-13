namespace Bevel.App.Supervision;

/// <summary>
/// One supervised role process — spawn, liveness, kill — behind an interface so the supervisor's
/// startup-order and crash-restart policy is tested by composition (a fake process + manual poll)
/// rather than by launching real OS processes. Mirrors <c>IHelperProcessHost</c>, the same seam the
/// helper monitor uses (bevel-gww.4).
/// </summary>
internal interface IRoleProcess : IDisposable
{
    /// <summary>Which surface this process hosts (Core, Taskbar, …). Drives startup ordering.</summary>
    ShellRole Role { get; }

    /// <summary>True while the process is running.</summary>
    bool IsAlive { get; }

    /// <summary>Spawns (or respawns) the process. A respawn re-execs the CURRENT binary, so the
    /// process comes back on the latest build — the mechanism behind "restart updates all".</summary>
    void Start();

    /// <summary>Kills the process (whole tree) if alive. Safe to call when nothing is running.</summary>
    void Kill();
}
