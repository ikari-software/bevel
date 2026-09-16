using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U5 (bevel-ncfp.5): the real Win32 <see cref="WindowsShellSession"/>. Two tiers:
/// <list type="bullet">
/// <item><b>Portable</b> facts assert the off-Windows contract (CI runs on macOS/Linux): reads are false,
/// actions are guarded no-ops that never throw and never touch the registry.</item>
/// <item><b>Windows-gated</b> tests (early-return off Windows) exercise the real registry round-trip for
/// run-at-login and the shutdown-privilege helper in isolation. They deliberately NEVER set Bevel as the
/// shell and NEVER call <see cref="WindowsShellSession.LogOutAsync"/> — those have machine-wide effects.</item>
/// </list>
/// </summary>
public class WindowsShellSessionTests
{
    // ── Portable contract (runs on every OS) ─────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_available_on_windows_only()
    {
        var caps = new WindowsShellSession().Capabilities;
        Assert.Equal(OperatingSystem.IsWindows(), caps.Available);
    }

    [Fact]
    public async Task Off_windows_reads_are_false()
    {
        if (OperatingSystem.IsWindows())
            return; // this fact pins the non-Windows behavior only

        var s = new WindowsShellSession();
        Assert.False(await s.IsRegisteredAsShellAsync());
        Assert.False(await s.IsRunAtLoginEnabledAsync());
    }

    [Fact]
    public async Task Off_windows_actions_are_no_throw_no_ops()
    {
        if (OperatingSystem.IsWindows())
            return;

        var s = new WindowsShellSession();
        // None of these may touch the registry or throw off Windows.
        await s.RegisterAsShellAsync();
        await s.UnregisterAsync();
        await s.SetRunAtLoginAsync(true);
        await s.SetRunAtLoginAsync(false);
        await s.LogOutAsync(LogoutKind.LogOut);
        await s.LogOutAsync(LogoutKind.Lock);
    }

    [Fact]
    public async Task Unregister_is_idempotent()
    {
        var s = new WindowsShellSession();
        // Off Windows: guarded no-op. On Windows: DeleteValue(throwOnMissingValue:false) — safe when absent.
        await s.UnregisterAsync();
        await s.UnregisterAsync();
    }

    // ── Windows-gated runtime behavior ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAtLogin_round_trips_through_the_Run_key()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var s = new WindowsShellSession();
        var wasEnabled = await s.IsRunAtLoginEnabledAsync();
        try
        {
            await s.SetRunAtLoginAsync(true);
            Assert.True(await s.IsRunAtLoginEnabledAsync());

            await s.SetRunAtLoginAsync(false);
            Assert.False(await s.IsRunAtLoginEnabledAsync());
        }
        finally
        {
            // Leave the machine as we found it.
            await s.SetRunAtLoginAsync(wasEnabled);
        }
    }

    [Fact]
    public void EnableShutdownPrivilege_succeeds_for_an_interactive_user()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // SE_SHUTDOWN_NAME is present (disabled) in a normal interactive token, so enabling it must succeed.
        // Isolated from any real ExitWindowsEx call — enabling a privilege has no side effect on its own.
        Assert.True(WindowsShellSession.EnableShutdownPrivilege());
    }

    [Fact]
    public void MachinePolicyShellOverride_probe_does_not_throw()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Reading the HKLM policy value is safe (read-only, no admin needed); on an unmanaged box it is absent.
        _ = WindowsShellSession.HasMachinePolicyShellOverride();
    }
}
