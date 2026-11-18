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

    /// <summary>Exit code for "another Bevel is serving" — the one the core already uses for "stood down".</summary>
    internal const int StandDownExitCode = 3;

    /// <summary>The launcher's go/no-go, decided with NO side effects (no spawn, no quit-marker or
    /// heartbeat write, no alert): null = proceed with boot, otherwise the message to show.</summary>
    internal static string? Decide(string controlSocket, string coreSocket, string recoverScript) =>
        FirstLiveSocket(controlSocket, coreSocket) is { } live
            ? $"another Bevel is still serving {live}; to recover, run {recoverScript}"
            : null;

    /// <summary>Stand-down side effects, kept apart from <see cref="Decide"/>: log + a native alert (the
    /// launcher has no window, and a Finder/login-item launch has no terminal — the same dialog the
    /// supervisor's Hold path raises, so the stop is never silent). <paramref name="alert"/> is the seam
    /// tests replace; production passes <see cref="ShellHealthAlert.ShowNative"/>, which is bounded
    /// (osascript wait capped at 15 s).</summary>
    internal static void Announce(string message, Action<string> log, Action<string, string> alert)
    {
        log(message);
        try { alert("Bevel", message + "\n\nThis launch is standing down; nothing was started."); }
        catch (Exception ex) { log($"stand-down alert failed: {ex.Message}"); }
    }

    /// <summary>Absolute path of <c>bevel-recover.sh</c> to name in the message: the bundled copy
    /// (<c>Contents/Resources</c>, next to <c>Contents/MacOS</c> = <paramref name="baseDirectory"/>) when
    /// running from a .app, else the repo copy found by walking up from the binary, else the
    /// repo-relative name as a last resort.</summary>
    internal static string RecoverScriptPath(string baseDirectory)
    {
        const string name = "bevel-recover.sh";
        var bundled = Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", name));
        if (File.Exists(bundled)) return bundled;
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var repo = Path.Combine(dir.FullName, "packaging", "macos", name);
            if (File.Exists(repo)) return repo;
        }
        return Path.Combine("packaging", "macos", name);
    }
}
