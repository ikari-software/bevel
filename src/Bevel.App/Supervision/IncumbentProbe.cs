using Bevel.ShellCore.Ipc;

namespace Bevel.App.Supervision;

/// <summary>
/// "Is another Bevel already serving?", answered BEFORE the launcher spawns any child (bevel-9790).
/// The instance lock covers the predecessor-teardown wait, but a lock and a socket are independent
/// detectors: the incumbent launcher can be gone while an orphaned core still serves <c>core.sock</c>
/// (the 2026-10-12 incident). A launcher that learns this only at <c>control.Start()</c> has already
/// spawned processes it must abandon. This is a plain connect probe — local, returns at once, never
/// blocks on a listener — and a stale socket FILE with no listener reads as "not running".
/// </summary>
internal static class IncumbentProbe
{
    /// <summary>The first of <paramref name="socketPaths"/> a live listener answers on, or null when
    /// every path is absent or stale (<see cref="UdsSocketClaim.IsServing"/> semantics).</summary>
    internal static string? FirstLiveSocket(params string[] socketPaths)
    {
        foreach (var path in socketPaths)
            if (!string.IsNullOrEmpty(path) && UdsSocketClaim.IsServing(path))
                return path;
        return null;
    }
}
