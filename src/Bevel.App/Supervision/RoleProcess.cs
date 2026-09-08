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
    // Windows only (bevel-ncfp.2): the named kernel event this child waits on for a graceful stop.
    // Freshly minted per Start so a restart never inherits an already-signaled event.
    private EventWaitHandle? _shutdownEvent;

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
        if (OperatingSystem.IsWindows())
            ArmWindowsShutdownEvent();
        _process = Process.Start(_startInfo)
            ?? throw new InvalidOperationException($"Failed to start {Role} process.");
        if (OperatingSystem.IsWindows())
            WindowsJobObject.TryAssign(_process); // orphan backstop — never the graceful path
    }

    public void Kill()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                // Graceful first: let the child run its own teardown — the taskbar restores the Dock,
                // the core stops the helper — instead of being torn down mid-state. macOS uses SIGTERM;
                // Windows Set()s the named shutdown event (WindowsShutdownSignal). Give it a short grace,
                // then hard-kill the whole tree if it hasn't exited (a wedged child must not block quit).
                if (!TryRequestGracefulStop() || !_process.WaitForExit(GraceMs))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited / never started */ }
        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        Kill();
        _shutdownEvent?.Dispose();
        _shutdownEvent = null;
    }

    private const int GraceMs = 3000;
    private const int Sigterm = 15;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ArmWindowsShutdownEvent()
    {
        _shutdownEvent?.Dispose();
        var name = @"Local\bevel-shutdown-" + Guid.NewGuid().ToString("N");
        _shutdownEvent = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, name);
        _startInfo.Environment[WindowsShutdownSignal.EnvVar] = name; // the child opens this by name
    }

    /// <summary>Asks the child to stop gracefully: SIGTERM on POSIX, Set() the named event on Windows.
    /// Returns false when there's no graceful channel, so the caller hard-kills instead.</summary>
    private bool TryRequestGracefulStop()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_shutdownEvent is null) return false;
            try { _shutdownEvent.Set(); return true; }
            catch { return false; }
        }
        try { return NativeKill(_process!.Id, Sigterm) == 0; }
        catch { return false; }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);
}
