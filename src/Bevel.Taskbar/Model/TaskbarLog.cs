using System.Diagnostics;

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

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Console.Error.WriteLine($"[TASKBAR] {message}");
    }
}
