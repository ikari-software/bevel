using Bevel.App;
using Bevel.Pal.Abstractions;

namespace Bevel.TestSupport;

/// <summary>
/// One in-memory <see cref="IWindowManager"/> for tests, in place of the per-file near-duplicate fakes.
/// Everything is a harmless no-op that records its call; subclasses override only what a test needs to
/// differ (fault injection, gating, richer recording). <see cref="IWindowManager.RestoreAndActivateAsync"/>
/// is deliberately NOT overridden: the interface default (Restore then Activate) is what fakes relied on.
/// The four events have <c>Raise*</c> helpers, so they are always "used" (no CS0067).
/// </summary>
public class TestWindowManager : IWindowManager
{
    /// <summary>For tests whose subject gates on <see cref="Capabilities.SupportsReposition"/>.</summary>
    public static Capabilities RepositionCapable { get; } =
        new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: [], SupportsReposition: true);

    public Capabilities Capabilities { get; init; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: []);

    /// <summary>What <see cref="EnumerateAsync"/> returns (a snapshot copy, taken under the lock).</summary>
    public List<ForeignWindow> Windows { get; } = [];

    public List<ForeignWindowId> Activated { get; } = [];
    public List<ForeignWindowId> Minimized { get; } = [];
    public List<ForeignWindowId> Restored { get; } = [];
    public List<ForeignWindowId> Closed { get; } = [];
    public List<ForeignWindowId> Zooms { get; } = [];
    public List<string> Hidden { get; } = [];
    public List<(ForeignWindowId Id, PalRect Bounds, PalRect? Expected)> Repositions { get; } = [];

    public virtual ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
    {
        lock (Windows) return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Windows.ToArray());
    }

    public virtual Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Record(Activated, id);
    public virtual Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Record(Minimized, id);
    public virtual Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Record(Restored, id);
    public virtual Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Record(Closed, id);
    public virtual Task ZoomAsync(ForeignWindowId id, CancellationToken ct = default) => Record(Zooms, id);

    public virtual Task HideAppAsync(string bundleId, CancellationToken ct = default) => Record(Hidden, bundleId);

    public virtual Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default)
        => Record(Repositions, (id, bounds, expected));

    private static Task Record<T>(List<T> list, T item)
    {
        lock (list) list.Add(item);
        return Task.CompletedTask;
    }

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;

    public void RaiseWindowOpened(ForeignWindow w) => WindowOpened?.Invoke(this, w);
    public void RaiseWindowClosed(ForeignWindow w) => WindowClosed?.Invoke(this, w);
    public void RaiseWindowChanged(ForeignWindow w) => WindowChanged?.Invoke(this, w);
    public void RaiseForegroundChanged(ForeignWindow w) => ForegroundChanged?.Invoke(this, w);
}

/// <summary>The window manager plus the app-environment and tray-host faces of one in-memory PAL, for
/// tests that wire all three to a single object. Same contract as <see cref="TestWindowManager"/>.</summary>
public class TestPlatform : TestWindowManager, IAppEnvironment, ISystemTrayHost
{
    public virtual ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RunningApp>>([]);
    public virtual ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<InstalledApp>>([]);
    public virtual Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => Task.CompletedTask;

    public virtual ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>([]);
    public virtual Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
        bool park = false, CancellationToken ct = default) => Task.FromResult(false);

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;

    public void RaiseAppLaunched(RunningApp a) => AppLaunched?.Invoke(this, a);
    public void RaiseAppTerminated(RunningApp a) => AppTerminated?.Invoke(this, a);
    public void RaiseInstalledAppsChanged(IReadOnlyList<InstalledApp> apps) => InstalledAppsChanged?.Invoke(this, apps);
    public void RaiseItemAdded(TrayItem i) => ItemAdded?.Invoke(this, i);
    public void RaiseItemRemoved(TrayItem i) => ItemRemoved?.Invoke(this, i);
    public void RaiseItemUpdated(TrayItem i) => ItemUpdated?.Invoke(this, i);
}

/// <summary>An <see cref="IPermissionBroker"/> that always reports (and grants) one fixed state.</summary>
public class TestPermissionBroker(PermissionState state) : IPermissionBroker
{
    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
        => ValueTask.FromResult(state);
    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
        => Task.FromResult(state);

    public event EventHandler<ShellPermission>? PermissionChanged;
    public void RaisePermissionChanged(ShellPermission p) => PermissionChanged?.Invoke(this, p);
}

/// <summary>An <see cref="IFilerSpawner"/> that records every spawn request instead of starting a Filer.
/// <see cref="Path"/>/<see cref="SelectPath"/>/<see cref="Search"/> mirror the most recent call.</summary>
public class TestFilerSpawner : IFilerSpawner
{
    public List<(string Path, string? Select, bool Search)> Calls { get; } = [];

    public string? Path { get; private set; }
    public string? SelectPath { get; private set; }
    public bool Search { get; private set; }

    public void Spawn(string path, string? selectPath = null, bool search = false)
    {
        Calls.Add((path, selectPath, search));
        Path = path;
        SelectPath = selectPath;
        Search = search;
    }
}
