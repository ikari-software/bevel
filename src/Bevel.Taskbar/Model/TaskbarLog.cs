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
}
