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

    // Session stamps (bevel-4zfs), guarded by _gate. Every event is stamped (epoch, seq). The EPOCH is
    // global: it bumps when the projection is rebuilt discontinuously (the startup seed), which also
    // re-pushes fresh snapshots to every client. The SEQ is PER-CLIENT and gapless: a global seq would let
    // one client's Hello burst consume numbers the others never see — phantom gaps, re-Hello cascade.
    // Stamping and enqueuing under the same lock keeps each client's frame order equal to its seq order.
    private int _epoch = 1;
    private readonly Dictionary<Guid, long> _seqByClient = new();

    /// <summary>Next per-client seq (caller holds <see cref="_gate"/>). Creates the counter on first use; a
    /// client that vanished leaves a stale long behind, pruned by <see cref="PruneStaleClients"/>.</summary>
    private long NextSeq(Guid clientId)
    {
        var seq = (_seqByClient.TryGetValue(clientId, out var s) ? s : 0) + 1;
        _seqByClient[clientId] = seq;
        return seq;
    }

    /// <summary>Drops per-client seq counters for clients no longer connected (reconnects get a fresh
    /// Guid, so without this the map grows with every link bounce). Amortized — runs when the map
    /// outgrows the live set. Caller holds <see cref="_gate"/>.</summary>
    private void PruneStaleClients()
    {
        if (_seqByClient.Count <= _server.ClientCount + 8) return;
        var live = _server.ClientIds;
        foreach (var id in _seqByClient.Keys.Where(id => !live.Contains(id)).ToArray())
            _seqByClient.Remove(id);
    }

    // Session accounting for diagnostics/tests: how many clients have started a session (Hello'd).
    private int _sessionsStarted;

    public ShellCoreServer(IWindowManager windows, IAppEnvironment apps, ISystemTrayHost tray,
        ISettingsService settings, string socketPath, byte[] nonce)
    {
        _windows = windows;
        _apps = apps;
        _tray = tray;
        _settings = settings;
        _server = new UdsMessageServer(socketPath, nonce, HandleRequestAsync);
    }

    /// <summary>Clients that have started a session (sent Hello) — diagnostics/tests. A connected-but-not-
    /// Hello'd client deliberately receives NOTHING: the session protocol (bevel-4zfs) exists so the CLIENT
    /// owns when the snapshot arrives, after its subscribers are attached.</summary>
    public int SessionsStarted => _sessionsStarted;

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
        // one snapshot a peer NEEDS synchronously at its first paint — the taskbar/Filer read Current to
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
        var initialApps = await SeedOrEmptyAsync(_apps.EnumerateInstalledAppsAsync, ct).ConfigureAwait(false);
        var initialTray = await SeedOrEmptyAsync(_tray.GetItemsAsync, ct).ConfigureAwait(false);
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

            // Seed complete = the projection was just rebuilt discontinuously: bump the epoch (bevel-4zfs)
            // and push fresh stamped snapshots to EVERY client. This replaces the old un-stamped
            // BroadcastSnapshot band-aid: a client that Hello'd during seeding got empty windows/tray,
            // and the deltas that fired during seeding were buffered — never broadcast. The epoch bump
            // tells clients the new burst is their authoritative baseline, and the snapshots self-heal
            // every projection (idempotent replaces; settings dedups by version).
            _epoch++;
            foreach (var evt in BuildSnapshotEvents())
                BroadcastStamped(evt);
        }
    }

    /// <summary>Runs <paramref name="enumerate"/>, tolerating the whole startup race window: the helper
    /// connects asynchronously after <c>host.Start()</c> AND can bounce mid-connect on a cold boot, so the
    /// first enumerate fails in more than one shape — <see cref="InvalidOperationException"/>("Helper not
    /// connected") before the channel exists, and a transport fault (e.g. gRPC <c>Cancelled</c> / "gRPC call
    /// disposed.") when the channel is torn down DURING the call. Seeding is best-effort by contract, so
    /// retry on ANY non-cancellation failure for ~10s, then degrade to an empty snapshot (the live delta
    /// streams + the window reconcile backstop repopulate). Never crashes the core — a throw here escapes to
    /// Main and aborts the process, which the supervisor then crash-loops (bevel-ejon).</summary>
    private static async ValueTask<IReadOnlyList<T>> SeedWhenHelperReadyAsync<T>(
        Func<CancellationToken, ValueTask<IReadOnlyList<T>>> enumerate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await enumerate(ct).ConfigureAwait(false); }
            // Scope the catch by INTENT (best-effort seed), not by exception type: typing it to
            // InvalidOperationException encoded an assumption about which transport error the helper
            // produces, and the gRPC-disposed shape slipped straight through it. Real cancellation
            // (shutdown) still propagates.
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                if (attempt >= 66)
                    return Array.Empty<T>();   // ~66 × 150ms ≈ 10s — streams + reconcile will fill it in
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Single-shot sibling of <see cref="SeedWhenHelperReadyAsync"/> for the snapshots taken once
    /// the helper is already up (apps/tray share its connection). Same contract — a failed seed degrades to
    /// empty and is repaired by the live streams; it must never abort the core — but no extra retry budget,
    /// so boot isn't lengthened by a genuinely unavailable source.</summary>
    private static async ValueTask<IReadOnlyList<T>> SeedOrEmptyAsync<T>(
        Func<CancellationToken, ValueTask<IReadOnlyList<T>>> enumerate, CancellationToken ct)
    {
        try { return await enumerate(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            return Array.Empty<T>();
        }
    }

    /// <summary>Builds the four projection snapshots (windows/apps/tray/settings) as of now, epoch-stamped
    /// (bevel-4zfs). Caller must hold <see cref="_gate"/> so the projection + epoch stay consistent; the
    /// per-client SEQ is stamped at send time by the push paths.</summary>
    private CoreEvent[] BuildSnapshotEvents()
    {
        var windowSnapshot = new CoreEvent(CoreEventKind.WindowSnapshot, Windows: _windowById.Values.ToArray(),
            Epoch: _epoch);
        var appSnapshot = new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: _installed,
            Epoch: _epoch);
        var traySnapshot = new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: _trayById.Values.ToArray(),
            Epoch: _epoch);
        // The settings blob lives in the service (its own single-writer discipline), not the _gate-guarded
        // projection — SnapshotJson/Version are cheap in-memory reads.
        var settingsSnapshot = new CoreEvent(CoreEventKind.SettingsSnapshot,
            SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version,
            Epoch: _epoch);
        return [windowSnapshot, appSnapshot, traySnapshot, settingsSnapshot];
    }

    /// <summary>Stamp + broadcast one event to EVERY client (bevel-4zfs). Under <see cref="_gate"/>, so
    /// per-client seq assignment and enqueue order can never invert — frame order on every client equals
    /// its own seq order. Every server→client event goes through here or <see cref="PushSnapshotTo"/>.
    /// Frames are serialized per client (seqs differ) — payloads are small; that is the price of gaplessness.</summary>
    private void BroadcastStamped(CoreEvent evt)
    {
        lock (_gate)
        {
            PruneStaleClients();
            foreach (var id in _server.ClientIds)
                _server.SendToClient(id, CoreProtocol.Serialize(evt with { Seq = NextSeq(id) }));
        }
    }

    /// <summary>Push a fresh stamped snapshot burst to ONE client (Hello/Resync, bevel-4zfs): the four
    /// snapshots get that client's next four gapless seqs, under <see cref="_gate"/>, so the burst cannot
    /// interleave against a concurrent <see cref="BroadcastStamped"/>. A delta landing before the burst
    /// applies to state the burst then replaces wholesale; one after it applies normally. Both converge.</summary>
    private void PushSnapshotTo(Guid clientId)
    {
        lock (_gate)
        {
            PruneStaleClients();
            foreach (var evt in BuildSnapshotEvents())
                _server.SendToClient(clientId, CoreProtocol.Serialize(evt with { Seq = NextSeq(clientId) }));
        }
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
        BroadcastStamped(new CoreEvent(CoreEventKind.WindowClosed, Window: w));
    }

    private void UpsertAndBroadcast(ForeignWindow w, CoreEventKind kind)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _windowById[w.Id.Value] = w); return; }
            _windowById[w.Id.Value] = w;
        }
        BroadcastStamped(new CoreEvent(kind, Window: w));
    }

    private void OnInstalledAppsChanged(object? _, IReadOnlyList<InstalledApp> apps)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _installed = apps); return; }
            _installed = apps;   // keep the connect-time snapshot fresh for future clients too
        }
        BroadcastStamped(new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: apps));
    }

    private void OnAppLaunched(object? _, RunningApp a) =>
        BroadcastStamped(new CoreEvent(CoreEventKind.AppLaunched, App: a));

    private void OnAppTerminated(object? _, RunningApp a) =>
        BroadcastStamped(new CoreEvent(CoreEventKind.AppTerminated, App: a));

    private void OnTrayItemAdded(object? _, TrayItem t) => UpsertTrayAndBroadcast(t, CoreEventKind.TrayItemAdded);
    private void OnTrayItemUpdated(object? _, TrayItem t) => UpsertTrayAndBroadcast(t, CoreEventKind.TrayItemUpdated);

    private void OnTrayItemRemoved(object? _, TrayItem t)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _trayById.Remove(t.Id.Value)); return; }
            _trayById.Remove(t.Id.Value);
        }
        BroadcastStamped(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: t));
    }

    private void UpsertTrayAndBroadcast(TrayItem t, CoreEventKind kind)
    {
        lock (_gate)
        {
            if (_seedBuffer is not null) { _seedBuffer.Add(() => _trayById[t.Id.Value] = t); return; }
            _trayById[t.Id.Value] = t;
        }
        BroadcastStamped(new CoreEvent(kind, TrayItem: t));
    }

    // ── UI commands -> real PAL ──────────────────────────────────────────
    private async ValueTask<byte[]> HandleRequestAsync(Guid clientId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        CoreResponse response;
        try
        {
            response = await ExecuteAsync(CoreProtocol.Deserialize<CoreCommand>(payload.Span), clientId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = CoreResponse.Fail(ex.Message);
        }
        return CoreProtocol.Serialize(response);
    }

    private async Task<CoreResponse> ExecuteAsync(CoreCommand cmd, Guid clientId, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            // Session protocol (bevel-4zfs): the snapshots arrive ONLY on the client's say-so — after it has
            // attached all its subscribers — and go to the ASKING client alone. Hello starts the session;
            // Resync re-baselines a late-attaching adapter or a gap-detected client.
            case CoreCommandKind.Hello:
                Interlocked.Increment(ref _sessionsStarted);
                PushSnapshotTo(clientId);
                return CoreResponse.Success();
            case CoreCommandKind.Resync:
                PushSnapshotTo(clientId);
                return CoreResponse.Success();
            // The tray's pull backstop (bevel-4zfs): every other projection had a pull; the tray was the
            // only stream-only one, which is exactly why its races were unhealable before.
            case CoreCommandKind.GetTrayItems:
                lock (_gate)
                    return new CoreResponse(Ok: true, TrayItems: _trayById.Values.ToArray());
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
                    cmd.TrayButton ?? TrayButton.Left, cmd.TrayModifiers ?? TrayModifiers.None, park: cmd.Park, ct: ct)
                    .ConfigureAwait(false);
                return new CoreResponse(Ok: true, Delivered: delivered);
            case CoreCommandKind.SetTrayHidden:
                // The native hide itself was applied in the UI process (bevel-qpir); this only forwards the
                // state to the helper so its tray-poll cadence adapts (fast while consolidated).
                await _tray.SetNativeTrayHiddenAsync(cmd.Hidden, ct).ConfigureAwait(false);
                return CoreResponse.Success();
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
                BroadcastStamped(new CoreEvent(CoreEventKind.SettingsChanged,
                    SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version));
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
