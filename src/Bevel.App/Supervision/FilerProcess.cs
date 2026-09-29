using System.Diagnostics;

namespace Bevel.App.Supervision;

/// <summary>
/// Production <see cref="IFilerProcess"/>: a <c>--role=filer</c> child launched DIRECTLY from the
/// launcher (never <c>/bin/sh</c>/<c>nohup</c>-detached — the TCC attribution breaker, Review
/// Focus #1 of bevel-t48y), with its stderr PUMPED to a per-pid file: a dying peer's managed stack
/// exists only in its stderr (<c>bevel-peer-process-crashes</c> — the pid-46808 filer crash was
/// diagnosed only because a nohup redirect caught it by luck). Graceful-then-hard kill mirrors
/// <see cref="RoleProcess.Kill"/>: SIGTERM first so the child's own teardown runs, then
/// <c>Kill(entireProcessTree)</c> after a short grace.
/// </summary>
internal sealed class FilerProcess : IFilerProcess
{
    private readonly ProcessStartInfo _startInfo;
    private Process? _process;

    public FilerProcess(ProcessStartInfo startInfo)
    {
        _startInfo = startInfo;
        _startInfo.RedirectStandardError = true; // the whole point: captured, not luck
    }

    /// <summary>Per-child stderr log — one file per pid, next to the restart diag log.</summary>
    public string StderrLogPath => RestartDiag.FilerLogPath(_process?.Id ?? 0);

    public bool IsAlive
    {
        get
        {
            var p = _process;
            if (p is null) return false;
            try
            {
                p.Refresh(); // cached HasExited would hide a dead filer from the monitor
                return !p.HasExited;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    public int? ExitCode
    {
        get
        {
            var p = _process;
            if (p is null) return null; // never started, or already reaped by our own Kill
            try
            {
                p.Refresh();
                return !p.HasExited ? null : p.ExitCode;
            }
            catch { return null; } // race with a signal death — reads as a crash, never as a close
        }
    }

    public void Start()
    {
        Kill(); // never leak a prior instance
        _process = Process.Start(_startInfo)
            ?? throw new InvalidOperationException("Failed to start filer process.");
        _process.ErrorDataReceived += OnStderrLine;
        _process.BeginErrorReadLine();
    }

    public void Kill()
    {
        var p = _process;
        if (p is null) return;
        try
        {
            p.Refresh();
            if (!p.HasExited)
            {
                if (!TryRequestGracefulStop(p) || !p.WaitForExit(GraceMs))
                    p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited / never started */ }
        finally
        {
            _process = null;
            p.Dispose();
        }
    }

    public void Dispose() => Kill();

    private const int GraceMs = 3000;
    private const int Sigterm = 15;

    private static void OnStderrLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        try
        {
            File.AppendAllText(
                RestartDiag.FilerLogPath(((Process)sender).Id),
                e.Data + Environment.NewLine);
        }
        catch { /* diagnostics must never throw, never kill the pump */ }
    }

    /// <summary>SIGTERM on POSIX (graceful: the child runs its own teardown). Windows children stop
    /// via the launcher's job object / hard kill — filers are user-closable surfaces with no
    /// shutdown-event contract, so the hard path there is the whole story.</summary>
    private static bool TryRequestGracefulStop(Process p)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        try { return NativeKill(p.Id, Sigterm) == 0; }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);
}
