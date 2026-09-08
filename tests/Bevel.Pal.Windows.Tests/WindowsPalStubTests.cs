using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// The small Windows PAL surfaces that stay simple across the whole port: permission brokering (no TCC),
/// tab provider (deferred for v1), volume labels (native DriveInfo pass-through), and audio (winmm, U9).
/// The substantial capabilities (window manager, app env, desktop, file stack, tray, shell session) have
/// their own dedicated *Tests files with portable + Windows-gated coverage — those assert the REAL
/// behavior, so this file no longer makes stub-shaped claims about them (which would fail on the
/// windows-latest CI runner once the real impls landed).
/// </summary>
public class WindowsPalStubTests
{
    [Fact]
    public async Task PermissionBroker_reports_NotApplicable_for_every_permission()
    {
        var pb = new WindowsPermissionBroker();
        foreach (var p in Enum.GetValues<ShellPermission>())
            Assert.Equal(PermissionState.NotApplicable, await pb.GetStateAsync(p));
    }

    [Fact]
    public void VolumeLabelSource_returns_null_so_callers_fall_back_to_the_mount_path()
        => Assert.Null(new WindowsVolumeLabelSource().LabelFor("C:\\"));

    [Fact]
    public void TabProvider_supports_nothing_yet()
        => Assert.False(new WindowsTabProvider().SupportsApp("com.google.Chrome"));

    // ── U9 audio (bevel-ncfp.9) — real winmm PlaySound, but portably testable behaviors ──────────

    [Fact]
    public async Task Audio_muted_is_a_no_op()
    {
        var a = new WindowsAudioPlayback { Muted = true };
        await a.PlayAsync("C:\\Windows\\Media\\ding.wav"); // muted → returns without touching winmm
    }

    [Fact]
    public async Task Audio_empty_path_and_missing_file_are_silent_no_ops()
    {
        var a = new WindowsAudioPlayback();
        await a.PlayAsync("");     // empty path: no-op
        await a.PlayAsync(null!);  // defensive: no throw
        // Off Windows the whole call is a guarded no-op; on Windows a bad path is swallowed. Either way
        // PlayAsync never throws — a theme sound must not crash the shell.
        await a.PlayAsync("Z:\\does\\not\\exist.wav");
    }
}
