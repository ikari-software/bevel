using System;
using System.IO;

namespace Bevel.App;

/// <summary>
/// Persistent, append-only trace of the restart/relaunch decision points (bevel restart debugging).
/// Writes to a FILE (not stdout) precisely because a relaunch detaches the child — its console output
/// is lost, but this survives across the exec boundary so we can see which path a "Restart" took
/// (launcher in-place restart vs. standalone re-exec) and whether the new process actually came up.
/// Cheap + swallow-on-failure; safe to call from any role/thread.
/// </summary>
internal static class RestartDiag
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "bevel-restart.log");

    public static void Log(string message)
    {
        try
        {
            var role = Environment.GetCommandLineArgs().Length > 1 ? string.Join(' ', Environment.GetCommandLineArgs()[1..]) : "(no-args)";
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:HH:mm:ss.fff}] pid={Environment.ProcessId} [{role}] {message}\n");
        }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Per-child stderr log for a supervised Filer (bevel-t48y): a dying peer's managed stack
    /// exists ONLY in its stderr, so each filer's stderr is pumped here instead of vanishing with the
    /// process (<c>bevel-peer-process-crashes</c>; the pid-46808 crash was caught only by luck).</summary>
    public static string FilerLogPath(int pid) => Path.Combine(Path.GetTempPath(), $"bevel-filer-{pid}.log");
}
