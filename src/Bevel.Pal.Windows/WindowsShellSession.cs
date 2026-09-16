using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bevel.Pal.Abstractions;
using Microsoft.Win32;

namespace Bevel.Pal.Windows;

/// <summary>
/// U5 (bevel-ncfp.5, HIGH-RISK spike): the real Win32 <see cref="IShellSession"/> —
/// set-as-shell (reversibly), run-at-login, and logout/restart/shutdown/lock.
///
/// <para><b>Set-as-shell</b> is the per-user kiosk mechanism (KTD-4): the current exe path written to
/// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon</c> value <c>Shell</c> (REG_SZ), which
/// Winlogon honors at logon on every edition (incl. Home). <b>Restore is DELETE of that HKCU value</b>
/// (Winlogon then falls back to the HKLM default, i.e. <c>explorer.exe</c>) — we never write
/// <c>explorer.exe</c> back into HKCU, which would freeze the fallback instead of releasing it. A machine
/// policy at <c>HKLM\...\Policies\System</c> value <c>Shell</c> overrides the per-user value on managed
/// boxes; we detect it (see <see cref="IsRegisteredAsShellAsync"/>) but the toggle still reflects HKCU.
/// Escape hatch if a broken shell locks you out: Ctrl+Alt+Del → Task Manager → Run new task → <c>explorer</c>
/// / <c>regedit</c> (see <c>packaging/windows/RESTORE-SHELL.md</c>).</para>
///
    /// <para><b>Power</b>: <see cref="ExitWindowsEx"/> for logoff/reboot/shutdown, <see cref="LockWorkStation"/>
    /// for lock. Logoff needs no privilege; reboot/shutdown need <c>SE_SHUTDOWN_NAME</c> enabled on the process
    /// token FIRST (<see cref="EnableShutdownPrivilege"/>). Privilege or Win32 failures throw so a rejected
    /// action cannot look like success (PR #1 #8).</para>
///
/// <para>Every registry / P/Invoke method is guarded with <see cref="OperatingSystem.IsWindows"/> so this
/// assembly loads and its constructor runs on CI's macOS/Linux runners (mirrors the bevel-8kxc discipline):
/// reads return <c>false</c> off Windows, actions no-op. No Win32 is touched in a constructor or static
/// initializer.</para>
/// </summary>
public sealed class WindowsShellSession : IShellSession
{
    // HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon : Shell  (per-user custom shell)
    private const string WinlogonSubKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ShellValueName = "Shell";

    // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System : Shell  (managed override; wins at logon)
    private const string PoliciesSystemSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run : Bevel  (run-at-login, parity with LoginItemRegistrar)
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Bevel";

    private static readonly Capabilities Caps = new(
        Available: true,
        TrayMode: TrayCapability.Authoritative,
        Notes: new[] { @"windows-shell-session: HKCU Winlogon\Shell + Run key + ExitWindowsEx" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    // ── Set-as-shell ─────────────────────────────────────────────────────────────────────────────

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult(false);
        return ValueTask.FromResult(IsRegisteredAsShell());
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRegisteredAsShell()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        // Report strictly on the HKCU per-user value (the thing our RegisterAsShell/Unregister owns).
        using var winlogon = Registry.CurrentUser.OpenSubKey(WinlogonSubKey);
        var hkcuShell = winlogon?.GetValue(ShellValueName) as string;
        var registered = string.Equals(hkcuShell, exe, StringComparison.OrdinalIgnoreCase);

        // Detect (but do NOT act on) a machine policy shell. If a non-empty value lives here, it OVERRIDES
        // the per-user Shell at logon on managed/kiosk boxes — so even a "true" here may not take effect.
        // We deliberately keep reporting HKCU state so the UI toggle mirrors what Bevel itself controls; a
        // caller that cares about the override can surface it separately.
        _ = HasMachinePolicyShellOverride();

        return registered;
    }

    /// <summary>True when <c>HKLM\...\Policies\System:Shell</c> is set to a non-empty custom shell, which
    /// overrides the per-user <c>Winlogon\Shell</c> at logon (managed boxes). Exposed for tests/diagnostics.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool HasMachinePolicyShellOverride()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(PoliciesSystemSubKey);
        var policyShell = policy?.GetValue(ShellValueName) as string;
        return !string.IsNullOrEmpty(policyShell);
    }

    public Task RegisterAsShellAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        RegisterAsShell();
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterAsShell()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return; // no self-path → nothing safe to register

        using var winlogon = Registry.CurrentUser.CreateSubKey(WinlogonSubKey, writable: true);
        winlogon.SetValue(ShellValueName, exe, RegistryValueKind.String);
    }

    public Task UnregisterAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        Unregister();
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void Unregister()
    {
        // DELETE the value (Winlogon falls back to the HKLM default). NEVER write "explorer.exe" back —
        // that pins the fallback in HKCU instead of releasing control. Idempotent: no throw if absent.
        using var winlogon = Registry.CurrentUser.OpenSubKey(WinlogonSubKey, writable: true);
        winlogon?.DeleteValue(ShellValueName, throwOnMissingValue: false);
    }

    // ── Run at login ─────────────────────────────────────────────────────────────────────────────

    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        SetRunAtLogin(enabled);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void SetRunAtLogin(bool enabled)
    {
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            using var run = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true);
            run.SetValue(RunValueName, exe, RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    public ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult(false);
        return ValueTask.FromResult(IsRunAtLoginEnabled());
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRunAtLoginEnabled()
    {
        // Presence of the value is the login-item state (reflects reality, not a persisted preference).
        using var run = Registry.CurrentUser.OpenSubKey(RunSubKey);
        return run?.GetValue(RunValueName) is not null;
    }

    // ── Power (logout / restart / shutdown / lock) ─────────────────────────────────────────────────

    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        LogOut(kind);
        return Task.CompletedTask; // advisory: the OS drives the actual sequence asynchronously
    }

    [SupportedOSPlatform("windows")]
    private static void LogOut(LogoutKind kind)
    {
        if (kind == LogoutKind.Lock)
        {
            if (!LockWorkStation())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LockWorkStation failed");
            return;
        }

        uint flags = kind switch
        {
            LogoutKind.LogOut => EWX_LOGOFF,
            LogoutKind.Restart => EWX_REBOOT,
            LogoutKind.Shutdown => EWX_SHUTDOWN,
            _ => EWX_LOGOFF,
        };

        if (kind is LogoutKind.Restart or LogoutKind.Shutdown)
        {
            if (!EnableShutdownPrivilege())
                throw new InvalidOperationException("SE_SHUTDOWN_NAME could not be enabled; restart/shutdown refused.");
        }

        if (!ExitWindowsEx(flags, SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ExitWindowsEx({kind}) failed");
    }

    /// <summary>Enables <c>SE_SHUTDOWN_NAME</c> on the current process token (required before
    /// <see cref="ExitWindowsEx"/> with EWX_SHUTDOWN/EWX_REBOOT). Returns true only when the privilege was
    /// actually assigned — <see cref="AdjustTokenPrivileges"/> can return TRUE yet leave
    /// <c>ERROR_NOT_ALL_ASSIGNED</c> in the last error, so we re-check GetLastError. Exposed internally so a
    /// Windows-gated test can exercise it in isolation without triggering a real shutdown.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges reports partial success via GetLastError, not its return value.
            return Marshal.GetLastWin32Error() == ERROR_SUCCESS;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public event EventHandler? SessionChanged; // contract event, not yet raised (CS0067 suppressed project-wide)

    // ── Win32 interop (private to this class; no shared Interop files) ─────────────────────────────

    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;

    // SHTDN_REASON_* — a planned "other" reason (major/minor both 0, planned flag set).
    private const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
    private const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    // Single-privilege TOKEN_PRIVILEGES: PrivilegeCount is fixed at 1 with the LUID+attributes inline, so we
    // avoid a variable-length array marshalling dance for the one privilege we ever toggle.
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);
}
