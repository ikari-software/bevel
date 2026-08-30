using Bevel.Core;
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
    // The core is the SOLE opener of settings.db (core-owns-settings, bevel-6nve): it snapshots this to
    // every UI process on connect and applies their changed-keys patches as the single writer, so no peer
    // touches SQLite. This is the real DB-backed SettingsService in the core role.
    private readonly ISettingsService _settings;
    private readonly UdsMessageServer _server;

    // The owned projection. A snapshot must be produced SYNCHRONOUSLY on connect (so the transport
    // enqueues it ahead of any concurrent delta), so current state lives in memory rather than being
    // re-enumerated per connect.
    private readonly object _gate = new();
    private readonly Dictionary<string, ForeignWindow> _windowById = new();
    private readonly Dictionary<string, TrayItem> _trayById = new();
    private IReadOnlyList<InstalledApp> _installed = Array.Empty<InstalledApp>();

    // Non-null ONLY during StartAsync's seeding window (bevel-8ck): PAL deltas that fire between
    // subscribing and applying the snapshot buffer their projection mutation here (guarded by _gate)
    // instead of racing the seed. After the snapshot is applied they replay in arrival order, then this
    // goes null and handlers mutate the projection directly.
    private List<Action>? _seedBuffer = new();

    public ShellCoreServer(IWindowManager windows, IAppEnvironment apps, ISystemTrayHost tray,
        ISettingsService settings, string socketPath, byte[] nonce)
    {
        _windows = windows;
        _apps = apps;
        _tray = tray;
        _settings = settings;
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
        // Subscribe BEFORE snapshotting (bevel-8ck). The PAL's delta streams are already live, so a
        // subscribe-AFTER-snapshot order dropped any add/remove that landed in the gap — permanently,
        // since (unlike windows) the tray has no reconcile backstop to re-derive it. While _seedBuffer
        // is non-null these handlers buffer their mutation instead of applying it.
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

        // Start SERVING before the (helper-dependent) window/tray seed below. Settings are already loaded
        // (Program.RunShellCore does settings.LoadAsync BEFORE constructing this server), and they are the
        // one snapshot a peer NEEDS synchronously at its first paint — the taskbar/Explorer read Current to
        // apply the theme immediately, and RemoteSettingsService.LoadAsync only waits ~5s before falling
        // back to DEFAULTS (losing the user's theme). Gating _server.Start() behind the ~2-10s helper
        // connect made peers time out and boot on defaults on a cold launch. So accept clients now: a peer
        // connecting mid-seed gets the correct settings snapshot immediately; its windows/tray fill in as
        // the seed completes and the 2s reconcile re-fetches (windows self-heal; PushSnapshot locks _gate,
        // so it never observes a half-applied projection).
        _server.Start();

        // host.Start() launches the Swift helper ASYNCHRONOUSLY (HelperLifecycle), so this initial
        // snapshot races the helper's gRPC connect: the first enumerate can throw
        // InvalidOperationException("Helper not connected"). A headless core MUST NOT crash on that race
        // (the design is graceful degradation) — otherwise the supervisor crash-loops the core forever in
        // split mode. Probe with the window enumerate, retrying briefly while the helper is still
        // connecting; once it's up, apps/tray share the same helper connection and enumerate cleanly.
        // Degrade to empty snapshots on timeout — the live streams + the window reconcile backstop then
        // repopulate. (~10s covers a cold helper exec + connect.)
        var initialWindows = await SeedWhenHelperReadyAsync(_windows.EnumerateAsync, ct).ConfigureAwait(false);
        IReadOnlyList<InstalledApp> initialApps;
        try { initialApps = await _apps.EnumerateInstalledAppsAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { initialApps = Array.Empty<InstalledApp>(); }
        IReadOnlyList<TrayItem> initialTray;
        try { initialTray = await _tray.GetItemsAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { initialTray = Array.Empty<TrayItem>(); }
        lock (_gate)
        {
            foreach (var w in initialWindows)
                _windowById[w.Id.Value] = w;
            _installed = initialApps;
            foreach (var t in initialTray)
                _trayById[t.Id.Value] = t;

            // Replay deltas buffered during the snapshot IN ORDER, then leave seeding mode — so an
            // add-then-remove in the gap ends removed, and an item removed after the snapshot was
            // taken doesn't linger as a ghost. From here handlers mutate the projection directly.
            foreach (var apply in _seedBuffer!)
                apply();
            _seedBuffer = null;
        }

        // Re-push the now-seeded projection to EVERY already-connected client. _server.Start() runs before
        // the (helper-dependent, ~2-10s) seed above so peers get settings promptly — but that means a
        // cold-boot peer connects DURING seeding and its on-connect PushSnapshot carried an EMPTY tray, and
        // ItemAdded/window deltas that fired during seeding were buffered (replayed into the projection
        // above) but never broadcast. Windows/apps self-heal via their reconcile backstops; the TRAY has
        // none ("the stream is the source of truth"), so without this a split taskbar strands on an empty
        // tray. Snapshots are idempotent — a client replaces its projection, settings dedups by version —
        // so a client that connected after seeding (already has the full snapshot) is unaffected.
        BroadcastSnapshot();
    }

    /// <summary>Runs <paramref name="enumerate"/>, tolerating the "Helper not connected" race at startup:
    /// the helper connects asynchronously after <c>host.Start()</c>, so the first enumerate can throw until
    /// it's up. Retries on that transient <see cref="InvalidOperationException"/> for ~10s, then degrades to
    /// an empty snapshot (the live delta streams + the window reconcile backstop repopulate). Never crashes
    /// the core.</summary>
    private static async ValueTask<IReadOnlyList<T>> SeedWhenHelperReadyAsync<T>(
        Func<CancellationToken, ValueTask<IReadOnlyList<T>>> enumerate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await enumerate(ct).ConfigureAwait(false); }
            catch (InvalidOperationException) when (attempt < 66 && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);   // ~66 × 150ms ≈ 10s
            }
            catch (InvalidOperationException)
            {
                return Array.Empty<T>();   // timed out — streams + reconcile will fill it in
            }
        }
    }

    /// <summary>Builds the four projection snapshots (windows/apps/tray/settings) as of now. Shared by the
    /// on-connect push and the post-seed re-broadcast so both send an identical, consistent set.</summary>
    private (CoreEvent Windows, CoreEvent Apps, CoreEvent Tray, CoreEvent Settings) BuildSnapshots()
    {
        CoreEvent windowSnapshot, appSnapshot, traySnapshot;
        lock (_gate)
        {
            windowSnapshot = new CoreEvent(CoreEventKind.WindowSnapshot, Windows: _windowById.Values.ToArray());
            appSnapshot = new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: _installed);
            traySnapshot = new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: _trayById.Values.ToArray());
        }
        // The settings blob lives in the service (its own single-writer discipline), not the _gate-guarded
        // projection, so it is snapshotted outside the lock. SnapshotJson/Version are cheap in-memory reads.
        var settingsSnapshot = new CoreEvent(CoreEventKind.SettingsSnapshot,
            SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version);
        return (windowSnapshot, appSnapshot, traySnapshot, settingsSnapshot);
    }

    // ── Snapshot on connect (synchronous — see the field comment) ────────
    private void PushSnapshot(Func<ReadOnlyMemory<byte>, ValueTask> sendToClient)
    {
        var (windows, apps, tray, settings) = BuildSnapshots();
        // Fire-and-forget: the transport funnels these through the client's ordered write channel,
        // so the snapshots (and any later broadcast) stay in order; a dead client is the
        // transport's problem, not ours.
        _ = sendToClient(CoreProtocol.Serialize(windows));
        _ = sendToClient(CoreProtocol.Serialize(apps));
        _ = sendToClient(CoreProtocol.Serialize(tray));
        _ = sendToClient(CoreProtocol.Serialize(settings));
    }

    /// <summary>Re-push the full projection to EVERY connected client (see the call site in StartAsync).</summary>
    private void BroadcastSnapshot()
    {
        var (windows, apps, tray, settings) = BuildSnapshots();
        _server.Broadcast(CoreProtocol.Serialize(windows));
        _server.Broadcast(CoreProtocol.Serialize(apps));
        _server.Broadcast(CoreProtocol.Serialize(tray));
        _server.Broadcast(CoreProtocol.Serialize(settings));
    }

    // ── PAL events -> projection update + delta broadcast ────────────────
    private void OnWindowOpened(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowOpened);
    private void OnWindowChanged(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowChanged);
    private void OnForegroundChanged(object? _, ForeignWindow w) => UpsertAndBroadcast(w, CoreEventKind.WindowForeground);

    private void OnWindowClosed(object? _, ForeignWindow w)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _windowById.Remove(w.Id.Value)); return; }
            _windowById.Remove(w.Id.Value);
        }
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.WindowClosed, Window: w)));
    }

    private void UpsertAndBroadcast(ForeignWindow w, CoreEventKind kind)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _windowById[w.Id.Value] = w); return; }
            _windowById[w.Id.Value] = w;
        }
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(kind, Window: w)));
    }

    private void OnInstalledAppsChanged(object? _, IReadOnlyList<InstalledApp> apps)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _installed = apps); return; }
            _installed = apps;   // keep the connect-time snapshot fresh for future clients too
        }
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
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _trayById.Remove(t.Id.Value)); return; }
            _trayById.Remove(t.Id.Value);
        }
        _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: t)));
    }

    private void UpsertTrayAndBroadcast(TrayItem t, CoreEventKind kind)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _trayById[t.Id.Value] = t); return; }
            _trayById[t.Id.Value] = t;
        }
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
            case CoreCommandKind.RestoreAndActivate:
                await _windows.RestoreAndActivateAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
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
            case CoreCommandKind.TerminateApp:
                await _windows.TerminateAppAsync(
                    cmd.AppIdOrPath ?? throw new ArgumentException("TerminateApp needs AppIdOrPath (bundle id)"),
                    cmd.Force, ct).ConfigureAwait(false);
                return CoreResponse.Success();
            case CoreCommandKind.LaunchApp:
                await _apps.LaunchAsync(cmd.AppIdOrPath ?? throw new ArgumentException("LaunchApp needs AppIdOrPath"), ct)
                    .ConfigureAwait(false);
                return CoreResponse.Success();
            case CoreCommandKind.ForwardTrayClick:
                var delivered = await _tray.ForwardClickAsync(
                    new TrayItemId(cmd.TrayItemId ?? throw new ArgumentException("ForwardTrayClick needs TrayItemId")),
                    cmd.TrayButton ?? TrayButton.Left, cmd.TrayModifiers ?? TrayModifiers.None, ct).ConfigureAwait(false);
                return new CoreResponse(Ok: true, Delivered: delivered);
            case CoreCommandKind.GetSettings:
                // The on-connect bootstrap pull (a peer that connected before the snapshot, or is
                // re-syncing) — hand back the current blob + version.
                return new CoreResponse(Ok: true, SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version);
            case CoreCommandKind.ApplySettingsUpdate:
                // The core is the sole writer: apply the peer's changed-keys merge patch, then broadcast the
                // fresh blob to EVERY UI process (including the sender) so they all re-project off one source.
                await _settings.ApplyPatchJsonAsync(
                    cmd.SettingsPatchJson ?? throw new ArgumentException("ApplySettingsUpdate needs SettingsPatchJson"),
                    ct).ConfigureAwait(false);
                _server.Broadcast(CoreProtocol.Serialize(new CoreEvent(CoreEventKind.SettingsChanged,
                    SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version)));
                return CoreResponse.Success();
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
