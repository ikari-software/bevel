using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bevel.App.Supervision;

/// <summary>
/// Windows graceful-teardown + orphan-backstop primitives for the split launcher (bevel-ncfp.2 / U2).
/// The macOS supervisor uses <c>SIGTERM → ctx.Cancel</c>; Windows has no equivalent signal a parent can
/// send a headless child (WM_CLOSE needs a window/pump the core lacks; console-ctrl events can't be
/// generated programmatically; .NET 10 no longer installs a default SIGTERM handler). So teardown is an
/// explicit handshake over a named kernel event, and a Job Object reaps any child the handshake misses.
///
/// <para>All P/Invoke is <see cref="OperatingSystem.IsWindows"/>-guarded at every call site so the
/// assembly loads and runs on CI's macOS/Linux runners without touching kernel32 (mirrors bevel-8kxc).</para>
/// </summary>
internal static class WindowsShutdownSignal
{
    /// <summary>Env var carrying the per-child named event the launcher signals to request a graceful
    /// stop (mirrors the existing <c>BEVEL_LAUNCHER_SOCKET</c> env plumbing).</summary>
    public const string EnvVar = "BEVEL_SHUTDOWN_EVENT";

    private sealed class NoOp : IDisposable { public static readonly NoOp Instance = new(); public void Dispose() { } }

    private sealed class Registration(EventWaitHandle handle, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            wait.Unregister(null);
            handle.Dispose();
        }
    }

    /// <summary>Child side: if the launcher passed a shutdown-event name, open it and run
    /// <paramref name="onStop"/> once it is signaled (the Windows analogue of the SIGTERM handler —
    /// wire it to the SAME teardown the SIGTERM handler drives). No-op off Windows or unsupervised.</summary>
    public static IDisposable Register(Action onStop)
    {
        if (!OperatingSystem.IsWindows()) return NoOp.Instance;
        var name = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(name)) return NoOp.Instance;
        if (!EventWaitHandle.TryOpenExisting(name, out var handle) || handle is null) return NoOp.Instance;

        // RegisterWaitForSingleObject fires onStop on a thread-pool thread when the parent Set()s the
        // event, without burning a dedicated blocking thread. executeOnlyOnce: shutdown happens once.
        var wait = ThreadPool.RegisterWaitForSingleObject(
            handle, (_, _) => onStop(), state: null, millisecondsTimeOutInterval: Timeout.Infinite, executeOnlyOnce: true);
        return new Registration(handle, wait);
    }
}

/// <summary>
/// A single launcher-owned Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every child
/// assigned to it dies when the launcher's handle closes (including a launcher crash), so a wedged or
/// missed child can never outlive the shell. This is the orphan BACKSTOP only — the named-event
/// handshake in <see cref="WindowsShutdownSignal"/> is the graceful path.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsJobObject
{
    private static readonly IntPtr Handle = CreateKillOnCloseJob();

    /// <summary>Assign a freshly-started child to the launcher job (best-effort; a child already in a
    /// non-nestable job — some CI hosts — just isn't assigned, and the graceful path still tears it down).</summary>
    public static void TryAssign(Process process)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, process.Handle); }
        catch { /* nested-job restriction or race — non-fatal; the named-event path is primary */ }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }

        return job;
    }

    // ── kernel32 interop ─────────────────────────────────────────────────────
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
