using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bevel.App.Supervision;

/// <summary>
/// Production <see cref="IRoleProcess"/>: launches <c>Environment.ProcessPath --role=&lt;role&gt;</c>
/// (or <c>dotnet Bevel.App.dll --role=…</c> when framework-dependent), preserving the <c>--pal=</c>
/// selector, and passing the launcher's PID down so the child can signal a whole-shell quit/restart.
/// Children are launched as DIRECT children of the launcher (not <c>nohup</c>-detached like the
/// single-process restart) so the supervisor can monitor their liveness and reap them on exit.
/// </summary>
internal sealed class RoleProcess : IRoleProcess
{
    private readonly ProcessStartInfo _startInfo;
    private Process? _process;

    public ShellRole Role { get; }
    public bool IsAlive => _process is { HasExited: false };

    public RoleProcess(ShellRole role, ProcessStartInfo startInfo)
    {
        Role = role;
        _startInfo = startInfo;
    }

    public void Start()
    {
        Kill(); // never leak a prior instance
        _process = Process.Start(_startInfo)
            ?? throw new InvalidOperationException($"Failed to start {Role} process.");
    }

    public void Kill()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                // Graceful first: SIGTERM lets the child run its own teardown — the taskbar restores the
                // Dock, the core stops the Swift helper — instead of being torn down mid-state. Give it a
                // short grace, then hard-kill the whole tree if it hasn't exited (a wedged child must not
                // block a restart/quit forever).
                if (!TrySigterm(_process.Id) || !_process.WaitForExit(GraceMs))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited / never started */ }
        _process.Dispose();
        _process = null;
    }

    public void Dispose() => Kill();

    private const int GraceMs = 3000;
    private const int Sigterm = 15;

    /// <summary>Sends SIGTERM to the child (POSIX). Returns false on non-Unix or on failure, so the
    /// caller falls back to a hard kill.</summary>
    private static bool TrySigterm(int pid)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return NativeKill(pid, Sigterm) == 0; }
        catch { return false; }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);
}
