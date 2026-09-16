namespace Bevel.App.Supervision;

/// <summary>
/// Cross-process "the user asked the shell to die" latch (bevel-0md2). The launcher-control
/// socket is the graceful path; this file is the backstop for when that send fails (stale
/// <c>launcher.sock</c>). The crash-monitor must not treat a subsequent child exit as a crash
/// and respawn it — that is how Start ▸ Quit became "restart the taskbar".
///
/// Written <b>before</b> the control send or the local shutdown so a monitor tick that lands
/// in the gap still sees the intent. Cleared at launcher boot so a leftover from a previous
/// session cannot suppress a legitimate crash-restart.
/// </summary>
internal static class QuitRequest
{
    public static string Path => System.IO.Path.Combine(BevelRuntimeDir.Root, "quit-requested");

    public static void Write()
    {
        try
        {
            Directory.CreateDirectory(BevelRuntimeDir.Root);
            File.WriteAllText(Path, "1");
        }
        catch { /* a failed write still leaves TrySend(Quit) / ShutdownLocal as fallbacks */ }
    }

    public static bool Exists()
    {
        try { return File.Exists(Path); }
        catch { return false; }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { /* stale file is worse than a leftover; next boot retries */ }
    }
}
