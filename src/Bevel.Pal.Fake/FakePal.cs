using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Fake;

// Deterministic in-memory implementations of every PAL interface. Everything here
// returns canned, side-effect-free data so the app can boot and tests can assert
// against a fixed scripted desktop. No randomness, no clocks, no I/O (DI-05/06).

internal static class FakeData
{
    public static Capabilities Caps { get; } = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "fake-pal" },
        SupportsReposition: true);
}

public sealed class FakeWindowManager : IWindowManager
{
    private static readonly IReadOnlyList<ForeignWindow> Windows = new[]
    {
        new ForeignWindow(new ForeignWindowId("w1"), "Untitled - Notepad", "fake.notepad", false, true, new PalRect(100, 100, 800, 600)),
        new ForeignWindow(new ForeignWindowId("w2"), "My Computer", "fake.explorer", false, false, new PalRect(200, 200, 1024, 768)),
    };

    public Capabilities Capabilities => FakeData.Caps;

    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Windows);

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}

public sealed class FakeSystemTrayHost : ISystemTrayHost
{
    private static readonly IReadOnlyList<TrayItem> Items = new[]
    {
        new TrayItem(new TrayItemId("t1"), "Volume"),
        new TrayItem(new TrayItemId("t2"), "Network"),
    };

    public Capabilities Capabilities => FakeData.Caps;

    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Items);

    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}

public sealed class FakeDesktopEnvironment : IDesktopEnvironment
{
    private static readonly IReadOnlyList<MonitorInfo> Monitors = new[]
    {
        new MonitorInfo(new MonitorId("m1"), 0, 0, 1920, 1080, true),
    };

    public Capabilities Capabilities => FakeData.Caps;

    public ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Monitors);

    public Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default)
        => Task.CompletedTask;

    public event EventHandler? MonitorsChanged;
}

public sealed class FakeShellSession : IShellSession
{
    public Capabilities Capabilities => FakeData.Caps;

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task RegisterAsShellAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task UnregisterAsync(CancellationToken ct = default) => Task.CompletedTask;
    private bool _runAtLogin;
    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default) { _runAtLogin = enabled; return Task.CompletedTask; }
    public ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default) => ValueTask.FromResult(_runAtLogin);
    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler? SessionChanged;
}

public sealed class FakeFileOperations : IFileOperations
{
    public Capabilities Capabilities => FakeData.Caps;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameAsync(string path, string newName, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeIconProvider : IIconProvider
{
    // A single fully-transparent 16x16 BGRA tile; deterministic and allocation-cheap.
    private static readonly PalImage Blank = new(16, 16, new byte[16 * 16 * 4]);

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        => ValueTask.FromResult(Blank);

    public event EventHandler? IconInvalidated;
}

public sealed class FakeAppEnvironment : IAppEnvironment
{
    private static readonly IReadOnlyList<RunningApp> Running = new[]
    {
        new RunningApp("fake.notepad", "Notepad", 1001),
    };

    private static readonly IReadOnlyList<InstalledApp> Installed = new[]
    {
        new InstalledApp("fake.notepad", "Notepad", null),
        new InstalledApp("fake.explorer", "Windows Explorer", null),
    };

    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Running);

    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Installed);

    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
}

public sealed class FakePermissionBroker : IPermissionBroker
{
    // The fake environment grants everything so zero-permission code paths and
    // granted code paths both exercise cleanly.
    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
        => ValueTask.FromResult(PermissionState.Granted);

    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
        => Task.FromResult(PermissionState.Granted);

    public event EventHandler<ShellPermission>? PermissionChanged;
}

public sealed class FakeAudioPlayback : IAudioPlayback
{
    public Capabilities Capabilities => FakeData.Caps;
    public bool Muted { get; set; }

    public Task PlayAsync(string wavPath, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeDockController : IDockController
{
    public Capabilities Capabilities => FakeData.Caps;

    public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
}
