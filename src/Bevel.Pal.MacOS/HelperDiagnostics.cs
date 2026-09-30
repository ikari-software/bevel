namespace Bevel.Pal.MacOS;

/// <summary>
/// Debug mirror for <c>BEVEL_DEBUG_WINDOWS=1</c>: surfaces the helper's <c>[BEVEL-WIN]</c> window
/// diagnostics to the app process. Pal may not reference App's RestartDiag, so the app wires this
/// sink at core startup to persist the lines — a GUI launch's stderr goes nowhere, and the
/// helper's per-window debug output is the only witness an AX/focus hunt has (the Kiro/Nessie
/// never-registers-as-active investigation).
/// </summary>
public static class HelperDiagnostics
{
    /// <summary>Raised on the drain thread for every [BEVEL-WIN] line the helper emits.</summary>
    public static event Action<string>? DebugLine;

    /// <summary>Drain-side raise (the event syntax restriction keeps subscribers honest).</summary>
    internal static void Raise(string line) => DebugLine?.Invoke(line);
}
