using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-platsettings: the settings dialog must not offer controls the running platform cannot act on.
/// On Windows it was showing "Accessibility Permission", "Screen Recording Permission", a Work-Area
/// Strategy described as handling "the macOS Dock", and a tray page about mirroring "the macOS menu-bar
/// icons" — all inert there. An inert control is worse than a missing one: it asks for a permission that
/// does not exist and reads as broken.
///
/// Gating is on PAL CAPABILITY rather than OperatingSystem.Is*, so these tests drive it by capability
/// and run identically on every CI runner.
/// </summary>
[Collection("TaskbarTheme")]
public class SettingsPlatformGatingTests
{
    private sealed class Broker(PermissionState state) : IPermissionBroker
    {
        public ValueTask<PermissionState> GetStateAsync(ShellPermission p, CancellationToken ct = default)
            => ValueTask.FromResult(state);
        public Task<PermissionState> RequestAsync(ShellPermission p, CancellationToken ct = default)
            => Task.FromResult(state);
        public event EventHandler<ShellPermission>? PermissionChanged;
    }

    private sealed class Dock(bool available) : IDockController
    {
        public Capabilities Capabilities { get; } =
            new(Available: available, TrayMode: TrayCapability.Authoritative, Notes: Array.Empty<string>());
        public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Tray(TrayCapability mode) : ISystemTrayHost
    {
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: mode, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;
    }

    private static SettingsService ScratchSettings()
        => new(Directory.CreateDirectory(Path.Combine(
               Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName);

    private static OnboardingWindow Build(PermissionState perms, bool dockAvailable, TrayCapability trayMode)
        => new(ScratchSettings(), new Broker(perms), null, new Dock(dockAvailable), new Tray(trayMode));

    /// The permission probe is async and fire-and-forget, so settle the dispatcher before asserting.
    private static async Task Settle()
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Windows_shaped_platform_hides_every_macOS_only_section()
    {
        var w = Build(PermissionState.NotApplicable, dockAvailable: false, TrayCapability.Authoritative);
        await Settle();

        Assert.False(w.AccessibilitySection.IsVisible);
        Assert.False(w.ScreenRecordingSection.IsVisible);
        Assert.False(w.WorkAreaSection.IsVisible);   // no Dock to have a strategy about
        Assert.False(w.TraySection.IsVisible);       // nothing to consolidate when the tray is ours
    }

    [AvaloniaFact]
    public async Task MacOS_shaped_platform_keeps_them_all()
    {
        var w = Build(PermissionState.Granted, dockAvailable: true, TrayCapability.Mirrored);
        await Settle();

        Assert.True(w.AccessibilitySection.IsVisible);
        Assert.True(w.ScreenRecordingSection.IsVisible);
        Assert.True(w.WorkAreaSection.IsVisible);
        Assert.True(w.TraySection.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_denied_permission_still_shows_its_section()
    {
        // Denied is the case the section EXISTS to fix — hiding it would strand the user with a broken
        // shell and no way to grant. Only NotApplicable means "this platform has no such permission".
        var w = Build(PermissionState.Denied, dockAvailable: true, TrayCapability.Mirrored);
        await Settle();

        Assert.True(w.AccessibilitySection.IsVisible);
        Assert.True(w.ScreenRecordingSection.IsVisible);
    }
}
