using Bevel.Pal.Abstractions;
using Bevel.ShellCore.Ipc;

namespace Bevel.App.ShellCore;

/// <summary>
/// The shell-core owner's server side (bevel-gww.3). It holds the SINGLE live projection of shell
/// state — the foreign-window list and the installed-app registry — seeded once from the real PAL
/// and kept current by that PAL's events, and serves it to every UI process over the UDS transport:
/// a snapshot the moment a process connects, then deltas. UI commands (activate/minimize/launch/…)
/// arrive as requests and are executed against the real PAL, so only THIS process talks to the
/// Swift helper / NSWorkspace — the UI processes never duplicate window enumeration or app watchers.
///
/// PAL-agnostic by construction: it depends only on <see cref="IWindowManager"/>/<see cref="IAppEnvironment"/>,
/// so the Fake PAL drives it end-to-end in tests without a helper or a GUI.
/// </summary>
public sealed class ShellCoreServer : IAsyncDisposable
{
    private readonly IWindowManager _windows;
    private readonly IAppEnvironment _apps;
    private readonly ISystemTrayHost _tray;
    private readonly UdsMessageServer _server;

    // The owned projection. A snapshot must be produced SYNCHRONOUSLY on connect (so the transport
    // enqueues it ahead of any concurrent delta), so current state lives in memory rather than being
    // re-enumerated per connect.
    private readonly object _gate = new();
    private readonly Dictionary<string, ForeignWindow> _windowById = new();
    private readonly Dictionary<string, TrayItem> _trayById = new();
    private IReadOnlyList<InstalledApp> _installed = Array.Empty<InstalledApp>();

    public ShellCoreServer(IWindowManager windows, IAppEnvironment apps, ISystemTrayHost tray,
        string socketPath, byte[] nonce)
    {
        _windows = windows;
        _apps = apps;
        _tray = tray;
        _server = new UdsMessageServer(socketPath, nonce, HandleRequestAsync);
        _server.ClientConnected += PushSnapshot;
    }

    /// <summary>Number of connected UI processes (for supervision/diagnostics).</summary>
    public int ClientCount => _server.ClientCount;

    /// <summary>
    /// Seeds the projection from the PAL, subscribes to its events, and starts accepting UI clients.
    /// The caller starts the window-manager poll (if the PAL has one) BEFORE this so the initial
    /// enumerate is warm — this method only reads current state and wires the delta stream.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var initialWindows = await _windows.EnumerateAsync(ct).ConfigureAwait(false);
        var initialApps = await _apps.EnumerateInstalledAppsAsync(ct).ConfigureAwait(false);
        var initialTray = await _tray.GetItemsAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var w in initialWindows)
                _windowById[w.Id.Value] = w;
            _installed = initialApps;
            foreach (var t in initialTray)
                _trayById[t.Id.Value] = t;
        }

        _windows.WindowOpened += OnWindowOpened;
        _windows.WindowClosed += OnWindowClosed;
        _windows.WindowChanged += OnWindowChanged;
        _windows.ForegroundChanged += OnForegroundChanged;
        _apps.AppLaunched += OnAppLaunched;
        _apps.AppTerminated += OnAppTerminated;
        _apps.InstalledAppsChanged += OnInstalledAppsChanged;
        _tray.ItemAdded += OnTrayItemAdded;
        _tray.ItemRemoved += OnTrayItemRemoved;
        _tray.ItemUpdated += OnTrayItemUpdated;

        _server.Start();
    }

    // ── Snapshot on connect (synchronous — see the field comment) ────────
    private void PushSnapshot(Func<ReadOnlyMemory<byte>, ValueTask> sendToClient)
    {
        CoreEvent windowSnapshot, appSnapshot, traySnapshot;
        lock (_gate)
        {
            windowSnapshot = new CoreEvent(CoreEventKind.WindowSnapshot, Windows: _windowById.Values.ToArray());
            appSnapshot = new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: _installed);
            traySnapshot = new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: _trayById.Values.ToArray());
        }
        // Fire-and-forget: the transport funnels these through the client's ordered write channel,
        // so the snapshots (and any later broadcast) stay in order; a dead client is the
        // transport's problem, not ours.
        _ = sendToClient(CoreProtocol.Serialize(windowSnapshot));
        _ = sendToClient(CoreProtocol.Serialize(appSnapshot));
        _ = sendToClient(CoreProtocol.Serialize(traySnapshot));
    }

    // ── PAL events -> projection update + delta broadcast ────────────────
    private void OnWindowOpened(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowOpened);
    private void OnWindowChanged(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowChanged);
    private void OnForegroundChanged(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowForeground);

    private void OnWindowClosed(object? _, ForeignWindow w)
    {
        lock (_gate)
            _windowById.Remove(w.Id.Value);
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.WindowClosed, Window: w)));
    }

    private void UpsertAndBroadcast(ForeignWindow w, CoreEventKind kind)
    {
        lock (_gate)
            _windowById[w.Id.Value] = w;
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(kind, Window: w)));
    }

    private void OnInstalledAppsChanged(object? _, IReadOnlyList<InstalledApp> apps)
    {
        lock (_gate)
            _installed = apps;   // keep the connect-time snapshot fresh for future clients too
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: apps)));
    }

    private void OnAppLaunched(object? _, RunningApp a) =>
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.AppLaunched, App: a)));

    private void OnAppTerminated(object? _, RunningApp a) =>
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.AppTerminated, App: a)));

    private void OnTrayItemAdded(object? _, TrayItem t) => UpsertTrayAndBroadcast(t, CoreEventKind.TrayItemAdded);
    private void OnTrayItemUpdated(object? _, TrayItem t) => UpsertTrayAndBroadcast(t, CoreEventKind.TrayItemUpdated);

    private void OnTrayItemRemoved(object? _, TrayItem t)
    {
        lock (_gate)
            _trayById.Remove(t.Id.Value);
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: t)));
    }

    private void UpsertTrayAndBroadcast(TrayItem t, CoreEventKind kind)
    {
        lock (_gate)
            _trayById[t.Id.Value] = t;
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(kind, TrayItem: t)));
    }

    // ── UI commands -> real PAL ──────────────────────────────────────────
    private async ValueTask<byte[]> HandleRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        CoreResponse response;
        try
        {
            response = await ExecuteAsync(CoreProtocol.Deserialize<CoreCommand>(payload.Span), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = CoreResponse.Fail(ex.Message);
        }
        return CoreProtocol.Serialize(response);
    }

    private async Task<CoreResponse> ExecuteAsync(CoreCommand cmd, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            case CoreCommandKind.EnumerateWindows:
                return new CoreResponse(Ok: true, Windows: await _windows.EnumerateAsync(ct).ConfigureAwait(false));
            case CoreCommandKind.Activate:
                await _windows.ActivateAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
            case CoreCommandKind.Minimize:
                await _windows.MinimizeAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
            case CoreCommandKind.Restore:
                await _windows.RestoreAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
            case CoreCommandKind.Close:
                await _windows.CloseAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
            case CoreCommandKind.Reposition:
                await _windows.RepositionAsync(Id(cmd),
                    cmd.Bounds ?? throw new ArgumentException("Reposition needs Bounds"), ct).ConfigureAwait(false);
                return CoreResponse.Success();
            case CoreCommandKind.CaptureWindow:
                return new CoreResponse(Ok: true, Png: await _windows.CaptureWindowAsync(
                    Id(cmd), cmd.MaxWidth ?? 0, cmd.MaxHeight ?? 0, ct).ConfigureAwait(false));
            case CoreCommandKind.EnumerateInstalledApps:
                return new CoreResponse(Ok: true, InstalledApps: await _apps.EnumerateInstalledAppsAsync(ct).ConfigureAwait(false));
            case CoreCommandKind.GetRunningApps:
                return new CoreResponse(Ok: true, RunningApps: await _apps.GetRunningAppsAsync(ct).ConfigureAwait(false));
            case CoreCommandKind.LaunchApp:
                await _apps.LaunchAsync(cmd.AppIdOrPath ?? throw new ArgumentException("LaunchApp needs AppIdOrPath"), ct)
                    .ConfigureAwait(false);
                return CoreResponse.Success();
            case CoreCommandKind.ForwardTrayClick:
                var delivered = await _tray.ForwardClickAsync(
                    new TrayItemId(cmd.TrayItemId ?? throw new ArgumentException("ForwardTrayClick needs TrayItemId")),
                    cmd.TrayButton ?? TrayButton.Left, cmd.TrayModifiers ?? TrayModifiers.None, ct).ConfigureAwait(false);
                return new CoreResponse(Ok: true, Delivered: delivered);
            default:
                return CoreResponse.Fail($"unknown command {cmd.Kind}");
        }
    }

    private static ForeignWindowId Id(CoreCommand cmd) =>
        new(cmd.WindowId ?? throw new ArgumentException($"{cmd.Kind} needs WindowId"));

    public async ValueTask DisposeAsync()
    {
        _windows.WindowOpened -= OnWindowOpened;
        _windows.WindowClosed -= OnWindowClosed;
        _windows.WindowChanged -= OnWindowChanged;
        _windows.ForegroundChanged -= OnForegroundChanged;
        _apps.AppLaunched -= OnAppLaunched;
        _apps.AppTerminated -= OnAppTerminated;
        _apps.InstalledAppsChanged -= OnInstalledAppsChanged;
        _tray.ItemAdded -= OnTrayItemAdded;
        _tray.ItemRemoved -= OnTrayItemRemoved;
        _tray.ItemUpdated -= OnTrayItemUpdated;
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
