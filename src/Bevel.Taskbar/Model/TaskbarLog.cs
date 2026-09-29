using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Bevel.Taskbar;

/// <summary>
/// Minimal diagnostic sink for the taskbar model layer's intentionally-swallowed exceptions.
/// A taskbar/menu action must never crash the shell, so these paths catch broadly — but a silent
/// catch leaves no trail when something fails for a non-transient reason. This routes those
/// swallowed exceptions to <see cref="Trace"/> (which the app's local diagnostic/crash logging
/// picks up) while preserving the UI-crash-safe behavior. Deliberately tiny — not a logging
/// framework; swap for the real one when it lands.
/// </summary>
internal static class TaskbarLog
{
    /// <summary>Records an exception that was caught and swallowed to keep the shell alive.</summary>
    public static void Swallowed(string context, Exception ex) =>
        Trace.TraceWarning($"[Bevel.Taskbar] swallowed in {context}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Opt-in (BEVEL_DEBUG_TASKBAR=1) trace of every window-model reason and every button
    /// collection change, so a live "phantom button / reflow" can be diagnosed from the log rather
    /// than by chasing transient screenshots. Written to stderr so it lands in the process log.</summary>
    private static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("BEVEL_DEBUG_TASKBAR") == "1";

    /// <summary>True when BEVEL_DEBUG_TASKBAR tracing is on. Guard callers that must build a
    /// message from non-trivial work (LINQ projections, joins) so that work is skipped when
    /// tracing is off — <see cref="Debug"/> alone still allocates its argument first.</summary>
    internal static bool IsEnabled => DebugEnabled;

    public static void Debug(string message)
    {
        if (!DebugEnabled) return;
        var line = $"[TASKBAR {DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.Error.WriteLine(line);
        WriteToFile(line);
    }

    /// <summary>
    /// Mirror of the stderr trace to a file, because the launch that can be DIAGNOSED and the launch
    /// that has the right PERMISSIONS are not the same launch.
    ///
    /// macOS attributes a TCC request to the RESPONSIBLE process, and a shell started from a terminal
    /// inherits that terminal as its responsible process — so Screen Recording and Accessibility get
    /// asked for on the terminal's behalf, not Bevel's. Anything permission-shaped can therefore only be
    /// reproduced from a Finder/launchd launch, where stderr goes nowhere and nothing reaches the
    /// unified log either. That combination cost real time on the tray-icon and menu-placement hunts.
    ///
    /// Best-effort by construction: a diagnostic sink must never be the thing that takes the shell down,
    /// so every failure here is swallowed. One file per process so concurrent shells cannot interleave.
    /// </summary>
    private static void WriteToFile(string line)
    {
        if (_file is null) return;
        try
        {
            lock (_fileLock) File.AppendAllText(_file, line + Environment.NewLine);
        }
        catch
        {
            // A full disk or a vanished directory must not kill the taskbar for the sake of a log line.
        }
    }

    private static readonly object _fileLock = new();

    private static readonly string? _file = OpenLogFile();

    private static string? OpenLogFile()
    {
        if (!DebugEnabled) return null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel", "logs");
            Directory.CreateDirectory(dir);
            var role = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--role=", StringComparison.Ordinal))?[7..] ?? "launcher";
            return Path.Combine(dir, $"taskbar-{role}-{Environment.ProcessId}.log");
        }
        catch { return null; }
    }

    /// <summary>Always-on (not debug-gated) line for a state the user can SEE but cannot explain from
    /// the bar alone — e.g. the tray drawing placeholders because no icon arrived (bevel-yduf). Callers
    /// must rate-limit themselves (log on change, not per poll); this is stderr, the process log.</summary>
    public static void Info(string message)
    {
        var line = $"[TASKBAR {DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.Error.WriteLine(line);
        WriteToFile(line);   // always-on lines matter MOST in a Finder launch, where stderr is discarded
    }
}
