using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U3 (bevel-ncfp.3) contract for the real Win32 <see cref="WindowsWindowManager"/>. The portable
/// facts run on every runner (macOS/Linux CI included): off Windows the whole P/Invoke surface is
/// guarded, so it constructs cleanly, reports unavailable, enumerates empty, and every action is an
/// inert no-op rather than a throw. The Windows-only facts are gated with an early return so the file
/// still compiles and passes off-Windows; on the box they assert real behavior (no crash, HWND ids
/// round-trip through <see cref="ForeignWindowId"/>).
/// </summary>
public class WindowsWindowManagerTests
{
    [Fact]
    public async Task Off_windows_is_unavailable_and_enumerates_empty()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        Assert.False(wm.Capabilities.Available);
        Assert.Empty(await wm.EnumerateAsync());
    }

    [Fact]
    public async Task Off_windows_every_action_is_an_inert_no_op()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        var id = new ForeignWindowId("12345");

        // None of these touch Win32 off-Windows — they must complete without throwing.
        await wm.ActivateAsync(id);
        await wm.MinimizeAsync(id);
        await wm.RestoreAsync(id);
        await wm.RestoreAndActivateAsync(id);
        await wm.CloseAsync(id);
        await wm.RepositionAsync(id, new PalRect(0, 0, 100, 100));
        await wm.TerminateAppAsync("C:\\Windows\\notepad.exe", force: false);
        Assert.Null(await wm.CaptureWindowAsync(id, 320, 240));
    }

    [Fact]
    public void Off_windows_subscribing_to_events_does_not_start_a_pump_or_throw()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        // The lazy pump start is Windows-guarded; wiring handlers is a pure no-op off Windows.
        void H(object? s, ForeignWindow w) { }
        wm.WindowOpened += H;
        wm.WindowClosed += H;
        wm.WindowChanged += H;
        wm.ForegroundChanged += H;
        wm.WindowOpened -= H;
        wm.WindowClosed -= H;
        wm.WindowChanged -= H;
        wm.ForegroundChanged -= H;
    }

    [Fact]
    public void Dispose_is_idempotent_on_any_os()
    {
        var wm = new WindowsWindowManager();
        wm.Dispose();
        wm.Dispose();   // second dispose must be a no-op, never throw
    }

    // ── Windows-only runtime facts ──────────────────────────────────────

    [Fact]
    public async Task Windows_is_available_and_supports_reposition()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        Assert.True(wm.Capabilities.Available);
        Assert.True(wm.Capabilities.SupportsReposition);
        // Enumerating the live desktop must not crash, and every id must round-trip to an HWND long.
        var windows = await wm.EnumerateAsync();
        foreach (var w in windows)
        {
            Assert.True(long.TryParse(w.Id.Value, out _), $"HWND id did not round-trip: '{w.Id.Value}'");
            Assert.NotNull(w.Id.Value);
        }
    }

    [Fact]
    public async Task Windows_actions_on_a_bogus_hwnd_do_not_throw()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        // A never-valid HWND: IsWindow gates every action, so these resolve to harmless no-ops.
        var id = new ForeignWindowId("999999999");
        await wm.ActivateAsync(id);
        await wm.MinimizeAsync(id);
        await wm.CloseAsync(id);
        await wm.RestoreAndActivateAsync(id);
        Assert.Null(await wm.CaptureWindowAsync(id, 200, 200));
    }
}
