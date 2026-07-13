using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bevel.App.ShellCore;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// End-to-end shell-core test (bevel-gww.3): a real <see cref="ShellCoreServer"/> over a live Unix
/// domain socket, driven by a controllable in-memory PAL, with the actual UI-side adapters
/// (<see cref="ShellCoreWindowManager"/> / <see cref="ShellCoreAppEnvironment"/>) as the client. It
/// proves the three things the split depends on: the core hands a NEW client its full snapshot on
/// connect, PAL deltas fan out to the client as ordinary <see cref="IWindowManager"/> events, and a
/// client command round-trips to the real PAL. PAL-agnostic — no helper, no GUI, no dispatcher.
/// </summary>
public sealed class ShellCoreIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private static string NewSocketPath()
        // macOS sun_path is 104 bytes — keep the stem short (mirrors the Ipc transport tests).
        => Path.Combine(Path.GetTempPath(), $"bvlcore-{Guid.NewGuid():N}"[..14] + ".sock");

    private static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(condition(), because);
    }

    private static ForeignWindow Win(string id, string title, bool focused = false) =>
        new(new ForeignWindowId(id), title, AppId: "com.example." + id, IsMinimized: false,
            IsFocused: focused, Bounds: new PalRect(0, 0, 800, 600));

    // 1. On connect, the core replays its whole window projection to the new client as WindowOpened
    //    events, and the installed-app registry is answerable by a pull — the snapshot path.
    [Fact]
    public async Task NewClient_ReceivesWindowSnapshot_AndInstalledApps()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"), Win("b", "Beta", focused: true));
        pal.SeedInstalled(new InstalledApp("com.example.a", "Alpha", IconPath: null));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var opened = new ConcurrentBag<string>();
        var wm = new ShellCoreWindowManager(core);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);

        // The snapshot is pushed the moment the connection authenticates; connect explicitly so the
        // adapters (which subscribed in their ctors) see it — nothing else has sent a command yet.
        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => opened.Count == 2, "snapshot should replay both seeded windows as WindowOpened");
        Assert.Equal(new[] { "a", "b" }, opened.OrderBy(x => x).ToArray());

        // Installed apps are a pull (round-trip), not a pushed event.
        var apps = new ShellCoreAppEnvironment(core);
        var installed = await apps.EnumerateInstalledAppsAsync(Ct);
        Assert.Equal("Alpha", Assert.Single(installed).DisplayName);
    }

    // 2. After connect, each PAL delta the core observes is re-raised on the client as the matching
    //    IWindowManager / IAppEnvironment event.
    [Fact]
    public async Task PalDeltas_FanOutToClient_AsWindowAndAppEvents()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var apps = new ShellCoreAppEnvironment(core);

        var opened = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource<RunningApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        wm.WindowOpened += (_, w) => opened.TrySetResult(w);
        wm.ForegroundChanged += (_, w) => foreground.TrySetResult(w);
        wm.WindowClosed += (_, w) => closed.TrySetResult(w);
        apps.AppLaunched += (_, a) => launched.TrySetResult(a);

        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => server.ClientCount == 1, "client should be connected before raising deltas");

        pal.RaiseWindowOpened(Win("z", "Zed"));
        Assert.Equal("Zed", (await opened.Task.WaitAsync(Timeout)).Title);

        pal.RaiseForegroundChanged(Win("z", "Zed", focused: true));
        Assert.True((await foreground.Task.WaitAsync(Timeout)).IsFocused);

        pal.RaiseWindowClosed(Win("z", "Zed"));
        Assert.Equal("z", (await closed.Task.WaitAsync(Timeout)).Id.Value);

        pal.RaiseAppLaunched(new RunningApp("com.example.z", "Zed", ProcessId: 4242));
        Assert.Equal(4242, (await launched.Task.WaitAsync(Timeout)).ProcessId);
    }

    // 3. A client command is executed against the REAL PAL (only the core touches it): activate a
    //    window, launch an app — the stub records each, proving the request round-trip.
    [Fact]
    public async Task ClientCommands_ExecuteAgainstRealPal()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var apps = new ShellCoreAppEnvironment(core);

        await wm.ActivateAsync(new ForeignWindowId("win-7"), Ct);
        await apps.LaunchAsync("/Applications/Calculator.app", Ct);

        await WaitFor(() => pal.Activated.Contains("win-7"), "core should have activated the window on the PAL");
        Assert.Contains("/Applications/Calculator.app", pal.Launched);
    }

    /// <summary>
    /// An in-memory PAL that plays both roles the core owns — window manager and app environment. It
    /// seeds the initial projection, lets a test raise deltas on demand, and records the commands the
    /// core forwards, so the whole server↔client path is exercised without a real platform backend.
    /// </summary>
    private sealed class ControllablePal : IWindowManager, IAppEnvironment
    {
        private readonly List<ForeignWindow> _windows = new();
        private IReadOnlyList<InstalledApp> _installed = Array.Empty<InstalledApp>();
        public ConcurrentBag<string> Activated { get; } = new();
        public ConcurrentBag<string> Launched { get; } = new();

        public void SeedWindows(params ForeignWindow[] windows) => _windows.AddRange(windows);
        public void SeedInstalled(params InstalledApp[] apps) => _installed = apps;

        public void RaiseWindowOpened(ForeignWindow w) => WindowOpened?.Invoke(this, w);
        public void RaiseWindowClosed(ForeignWindow w) => WindowClosed?.Invoke(this, w);
        public void RaiseWindowChanged(ForeignWindow w) => WindowChanged?.Invoke(this, w);
        public void RaiseForegroundChanged(ForeignWindow w) => ForegroundChanged?.Invoke(this, w);
        public void RaiseAppLaunched(RunningApp a) => AppLaunched?.Invoke(this, a);
        public void RaiseAppTerminated(RunningApp a) => AppTerminated?.Invoke(this, a);

        // ── IWindowManager ──
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(_windows.ToArray());

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;

        // ── IAppEnvironment ──
        public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

        public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(_installed);

        public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) { Launched.Add(appIdOrPath); return Task.CompletedTask; }

        public event EventHandler<RunningApp>? AppLaunched;
        public event EventHandler<RunningApp>? AppTerminated;
    }
}
