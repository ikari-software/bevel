using System.Globalization;

namespace Bevel.App.Supervision;

/// <summary>
/// The shell's single-instance guard (bevel-wio0): an exclusive file lock on
/// <c>~/.config/bevel/shell.lock</c> that the launcher takes before it does ANYTHING else and holds
/// for its whole lifetime. A second launcher can't take it and exits, reporting the incumbent —
/// instead of booting a second core + taskbar that fight the first over one settings.db (the
/// five-process tug-of-war of 2026-09-29: rows reverting, theme reverting, tray going blank).
///
/// <para>Mechanism. <see cref="FileShare.None"/> is <c>flock(LOCK_EX | LOCK_NB)</c> on macOS/Linux and
/// an exclusive share on Windows. Both are released by the kernel when the owner dies, however it
/// dies — so a crashed launcher never wedges the next start, and no pid-liveness heuristics are
/// needed. The incumbent's pid goes in a sibling <c>shell.pid</c> file (NOT the lock file: .NET's
/// Unix file locking would refuse to open a <c>LOCK_EX</c>-held file even for reading) so the loser
/// can name it — and a future <c>--takeover</c> can signal it.</para>
///
/// <para>Order matters at the call site: the launcher must hold this BEFORE clearing the quit marker
/// / heartbeats or binding <c>launcher.sock</c>, all of which would otherwise clobber the live
/// shell's state.</para>
/// </summary>
internal sealed class ShellInstanceLock : IDisposable
{
    /// <summary>The production lock path. Resolves the real config dir (throws in a test host).</summary>
    public static string DefaultPath => System.IO.Path.Combine(Bevel.Core.BevelConfigDir.Path, "shell.lock");

    /// <summary>How long a launcher waits for a predecessor that is still tearing down (a
    /// <c>pkill</c>-then-relaunch script, a quit followed by a click) before giving up.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(3);

    private readonly FileStream _lock;
    private bool _disposed;

    public string Path { get; }
    public string PidPath => PidPathFor(Path);

    private ShellInstanceLock(FileStream @lock, string path)
    {
        _lock = @lock;
        Path = path;
    }

    /// <summary>
    /// Tries to become THE shell. Retries for up to <paramref name="wait"/> (poll 100 ms) so a launch
    /// that lands while the previous shell is still exiting succeeds instead of reporting a ghost.
    /// Returns the held lock, or <c>null</c> with <paramref name="incumbentPid"/> set to the running
    /// shell's pid (0 when unknown).
    /// </summary>
    public static ShellInstanceLock? TryAcquire(string path, TimeSpan wait, out int incumbentPid)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(dir);
            else
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            try
            {
                var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var held = new ShellInstanceLock(fs, path);
                held.RecordPid();
                incumbentPid = 0;
                return held;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(100); // the previous shell may still be releasing
            }
            catch (IOException)
            {
                incumbentPid = ReadIncumbentPid(path);
                return null;
            }
        }
    }

    /// <summary>The pid recorded by whoever holds <paramref name="lockPath"/>, or 0 if unreadable.</summary>
    public static int ReadIncumbentPid(string lockPath)
    {
        try
        {
            var text = File.ReadAllText(PidPathFor(lockPath)).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string PidPathFor(string lockPath) => lockPath + ".pid";

    private void RecordPid()
    {
        try { File.WriteAllText(PidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)); }
        catch { /* diagnostic only — the lock itself is what excludes a second shell */ }
    }

    /// <summary>Releases the lock. The pid file is removed too so a stale pid never names a process
    /// that no longer exists (the kernel drops the lock on crash regardless).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(PidPath); } catch { /* best effort */ }
        _lock.Dispose();
    }
}
