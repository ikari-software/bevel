using System.Diagnostics;
using Bevel.App.Supervision;
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

    /// <summary>True once the tray projection is trustworthy: the seed succeeded, or a live tray add/update
    /// arrived. Until then no TraySnapshot is sent — a degraded (failed-seed, empty) projection would make a
    /// surviving taskbar's reconcile remove every icon it holds (bevel-ri8c). Guarded by <see cref="_gate"/>.</summary>
    private bool _traySeeded;

    /// <summary>The window twin of <see cref="_traySeeded"/> (bevel-wheq): true once the window projection is
    /// trustworthy — the seed reached the window source, or a live open/change/foreground delta arrived. Until
    /// then no WindowSnapshot is sent, because a surviving taskbar now closes every button a snapshot omits,
    /// and a seed that could not reach the helper is an empty projection, not "no windows are open".
    /// Guarded by <see cref="_gate"/>.</summary>
    private bool _windowsSeeded;

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
        // Degrade to empty snapshots at the seed deadline — the live streams + the window reconcile backstop
        // then repopulate. A degraded seed is not published as a snapshot (bevel-wheq): see _windowsSeeded.
        // ONE deadline covers the window AND tray seeds (bevel-wheq review): the core only reports ready after
        // this method returns, so two back-to-back full budgets would outlast the launcher's ready timeout and
        // get a degraded-but-working core restarted, then Held. See DefaultSeedDeadline.
        var seedDeadline = Stopwatch.GetTimestamp() + (long)(SeedDeadline.TotalSeconds * Stopwatch.Frequency);
        var (initialWindows, windowsSeedOk) =
            await SeedUntilAnsweredAsync(_windows.TrySeedWindowsAsync, seedDeadline, ct).ConfigureAwait(false);

        // Apps and tray CONCURRENTLY, not one after the other (bevel-pa3j). They are independent reads
        // of two different sources, and nothing between them orders one before the other — but the
        // commit below (and therefore the snapshot burst every peer is waiting on) was gated on their
        // SUM. They still start only after the windows seed, because that call is also the helper
        // readiness probe: apps and tray share the helper's connection, and apps has no retry budget of
        // its own, so running them against a half-connected helper would just degrade them to empty.
        var appsSeed = SeedOrEmptyAsync(_apps.EnumerateInstalledAppsAsync, ct).AsTask();
        // The tray retries "not yet" too, but only inside what is left of the SAME deadline — and not at all
        // when the window probe never got an answer: the helper is unreachable, and asking its tray service
        // would only spend more of the core's ready budget on the same verdict (withheld either way).
        var traySeed = windowsSeedOk
            ? SeedUntilAnsweredAsync(_tray.TrySeedItemsAsync, seedDeadline, ct)
            : Task.FromResult<(IReadOnlyList<TrayItem>, bool)>((Array.Empty<TrayItem>(), false));
        var initialApps = await appsSeed.ConfigureAwait(false);
        var (initialTray, traySeedOk) = await traySeed.ConfigureAwait(false);
        lock (_gate)
        {
            // A FAILED seed is not an authoritative empty tray (bevel-ri8c): withhold TraySnapshot until a
            // seed has succeeded or a live tray delta proves the stream is up. A successful seed that
            // returned zero items IS authoritative.
            if (traySeedOk) _traySeeded = true;
            // Same for windows (bevel-wheq): a seed that never reached the helper withholds WindowSnapshot.
            if (windowsSeedOk) _windowsSeeded = true;
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
            // BroadcastSnapshot band-aid: a client that Hello'd during seeding got only apps+settings
            // (window/tray are withheld mid-seed, bevel-ri8c) and its windows/tray arrive in THIS burst,
            // and the deltas that fired during seeding were buffered — never broadcast. The epoch bump
            // tells clients the new burst is their authoritative baseline, and the snapshots self-heal
            // every projection (idempotent replaces; settings dedups by version).
            _epoch++;
            foreach (var evt in BuildSnapshotEvents())
                BroadcastStamped(evt);
        }
    }

    /// <summary>The shared window+tray seed deadline (bevel-wheq review). The core calls
    /// <c>RoleHeartbeatStore.ReportReady</c> only after <see cref="StartAsync"/> returns, and the launcher restarts
    /// a role that is not ready within <see cref="ShellHealthMonitor.DefaultReadyTimeout"/> (15 ticks × 1 s = 15 s;
    /// three such restarts → Hold). Half of it (7.5 s) still covers a cold helper exec + connect, and leaves the
    /// other half for host start, settings load, the apps seed and the snapshot commit. Pinned by
    /// <c>The_seed_deadline_ends_well_inside_the_launchers_ready_timeout</c>.</summary>
    internal static TimeSpan DefaultSeedDeadline => ShellHealthMonitor.DefaultReadyTimeout / 2;

    /// <summary>Overridable for tests whose source never answers, so they need not wait the real deadline.</summary>
    internal TimeSpan SeedDeadline { get; init; } = DefaultSeedDeadline;

    private static readonly TimeSpan SeedRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Runs a seed-grade read (<see cref="IWindowManager.TrySeedWindowsAsync"/> /
    /// <see cref="ISystemTrayHost.TrySeedItemsAsync"/>) until the source ANSWERS or <paramref name="deadline"/>
    /// (a <see cref="Stopwatch"/> timestamp) passes, and reports whether it answered. The helper connects
    /// asynchronously after <c>host.Start()</c> AND can bounce mid-connect on a cold boot, so a read fails in
    /// several shapes: <see cref="InvalidOperationException"/>("Helper not connected") before the channel exists;
    /// a transport fault (gRPC <c>Cancelled</c> / "gRPC call disposed.") when the channel is torn down DURING the
    /// call; and a <c>null</c> answer — gRPC <c>Unavailable</c>, which the macOS PAL softens — because the helper
    /// host opens the channel BEFORE its first Ping succeeds, so a channel can exist while the helper is not yet
    /// serving. All three mean "not yet" and are retried alike. Each attempt is also bounded by the deadline, so
    /// a wedged call cannot hold startup past it. At the deadline the seed degrades to an empty projection
    /// reported as NOT answered — which the caller must not publish as authoritative (_windowsSeeded /
    /// _traySeeded); the live delta streams + the window reconcile backstop repopulate. Never crashes the core —
    /// a throw here escapes to Main and aborts the process, which the supervisor then crash-loops (bevel-ejon).
    /// Real cancellation (<paramref name="ct"/>, shutdown) still propagates.
    /// </summary>
    private static async Task<(IReadOnlyList<T> Items, bool Ok)> SeedUntilAnsweredAsync<T>(
        Func<CancellationToken, ValueTask<IReadOnlyList<T>?>> trySeed, long deadline, CancellationToken ct)
    {
        while (true)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
                return (Array.Empty<T>(), false);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(remaining);
                if (await trySeed(attempt.Token).ConfigureAwait(false) is { } items)
                    return (items, true);
            }
            // Scope the catch by INTENT (best-effort seed), not by exception type: typing it to
            // InvalidOperationException encoded an assumption about which transport error the helper
            // produces, and the gRPC-disposed shape slipped straight through it. An attempt cut off by the
            // deadline lands here too (its own token, not ct); only a real shutdown escapes.
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
                return (Array.Empty<T>(), false);
            await Task.Delay(remaining < SeedRetryDelay ? remaining : SeedRetryDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Single-shot sibling of <see cref="SeedUntilAnsweredAsync{T}"/> for the installed-apps snapshot,
    /// taken once the helper is already up. Same contract — a failed seed degrades to
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
            Epoch: _epoch, WindowCapabilities: _windows.Capabilities);   // bevel-mhvu: forward, don't duplicate
        var appSnapshot = new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: _installed,
            Epoch: _epoch);
        var traySnapshot = new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: _trayById.Values.ToArray(),
            Epoch: _epoch, TrayCapabilities: _tray.Capabilities);   // bevel-mhvu
        // The settings blob lives in the service (its own single-writer discipline), not the _gate-guarded
        // projection — SnapshotJson/Version are cheap in-memory reads.
        var settingsSnapshot = new CoreEvent(CoreEventKind.SettingsSnapshot,
            SettingsJson: _settings.SnapshotJson(), SettingsVersion: _settings.Version,
            Epoch: _epoch);
        // No window/tray snapshot until that projection is real (bevel-wheq / bevel-ri8c): see _windowsSeeded
        // and _traySeeded. Omitted events take no seq, so a client's sequence stays gapless.
        var events = new List<CoreEvent>(4);
        if (_windowsSeeded) events.Add(windowSnapshot);
        events.Add(appSnapshot);
        if (_traySeeded) events.Add(traySnapshot);
        events.Add(settingsSnapshot);
        return events.ToArray();
    }

    /// <summary>Test seam (bevel-b79z): the next N broadcast frames are stamped (consuming their per-client
    /// seq) but never sent — a genuinely lost delta, so a test can prove the client re-Hellos on a REAL gap.</summary>
    internal int DropNextBroadcastFramesForTest;

    /// <summary>The ONE place a server→client frame is stamped and sent (caller holds <see cref="_gate"/>):
    /// the CURRENT epoch (an event that already carries one, i.e. a snapshot, keeps it) plus the client's next
    /// seq. The client's sequence tracker ignores any event lacking an epoch, so an epoch-less frame would
    /// consume a seq the client never records and make the next frame look like a lost one — a phantom gap
    /// and a spurious re-Hello (bevel-b79z). Every send path goes through here so that rule cannot diverge.</summary>
    private void SendStampedLocked(Guid clientId, CoreEvent evt)
    {
        var stamped = evt with { Epoch = evt.Epoch ?? _epoch, Seq = NextSeq(clientId) };
        if (DropNextBroadcastFramesForTest > 0 && evt.Epoch is null)
        {
            DropNextBroadcastFramesForTest--;   // delta lost in transit: seq consumed, frame never sent
            return;
        }
        _server.SendToClient(clientId, CoreProtocol.Serialize(stamped));
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
                SendStampedLocked(id, evt);
        }
    }

    /// <summary>Push a fresh stamped snapshot burst to ONE client (Hello/Resync, bevel-4zfs): the snapshots
    /// it carries (window/tray only once earned) get that client's next gapless seqs, under <see cref="_gate"/>, so the burst cannot
    /// interleave against a concurrent <see cref="BroadcastStamped"/>. A delta landing before the burst
    /// applies to state the burst then replaces wholesale; one after it applies normally. Both converge.</summary>
    private void PushSnapshotTo(Guid clientId)
    {
        lock (_gate)
        {
            PruneStaleClients();
            foreach (var evt in BuildSnapshotEvents())
            {
                // A Hello answered while the core is still seeding would carry an EMPTY window/tray
                // projection, and a reconciling client would wipe its state on it (bevel-ri8c). Omit
                // those two: the epoch-bump burst at seed completion follows and is authoritative.
                // Skipped events take no seq, so the client's sequence stays gapless.
                if (_seedBuffer is not null
                    && evt.Kind is CoreEventKind.WindowSnapshot or CoreEventKind.TraySnapshot)
                    continue;
                SendStampedLocked(clientId, evt);
            }
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
            if (_seedBuffer is not null) { _seedBuffer.Add(() => Forget(w.Id.Value)); return; }
            Forget(w.Id.Value);
        }
        BroadcastStamped(new CoreEvent(CoreEventKind.WindowClosed, Window: w));
    }

    /// <summary>Drops a window from the projection AND forgets its last-sent icon, so an id that
    /// comes back (macOS reuses window numbers) re-sends its icon instead of inheriting a stale
    /// "already sent" claim. Caller holds <c>_gate</c>.</summary>
    private void Forget(string id)
    {
        _windowById.Remove(id);
        _sentIconHash.Remove(id);
    }

    private void UpsertAndBroadcast(ForeignWindow w, CoreEventKind kind)
    {
        ForeignWindow outbound;
        lock (_gate)
        {
            // A buffered open/change/foreground is just as live as one after seeding (bevel-wheq review): it
            // unlocks the snapshot on replay, or a failed seed on a quiet desktop would withhold a complete
            // projection until some later delta. A buffered CLOSE (OnWindowClosed) still does not.
            if (_seedBuffer is not null)
            {
                _seedBuffer.Add(() => { _windowById[w.Id.Value] = w; _windowsSeeded = true; });
                return;
            }
            _windowById[w.Id.Value] = w;   // the PROJECTION always keeps the icon, so snapshots carry it
            // An open/change/foreground proves the window stream is live (bevel-wheq), so later snapshots are
            // trustworthy enough to publish. A close alone proves nothing (OnWindowClosed does not set it).
            _windowsSeeded = true;
            outbound = WithoutUnchangedIcon(w);
        }
        BroadcastStamped(new CoreEvent(kind, Window: outbound));
    }

    /// <summary>Last icon we actually sent for a window, by id. Guarded by <c>_gate</c>.</summary>
    private readonly Dictionary<string, long> _sentIconHash = new(StringComparer.Ordinal);

    /// <summary>
    /// Drops <see cref="ForeignWindow.IconPng"/> from an outbound delta when it is byte-identical to
    /// the icon this window's last delta already carried (bevel-ougs).
    ///
    /// <para>WindowChanged fires on every title change — which for a browser or an editor is
    /// essentially every keystroke — and each one base64'd the app's icon into the JSON frame again.
    /// The icon is per-APP and practically never changes.</para>
    ///
    /// <para>Safe for the one consumer that reads it: <c>ShellModel.ApplyUpdate</c> re-attempts an icon
    /// only while the button still has none, and its loader no-ops on a null PNG, so an omitted icon
    /// never blanks a good one. The case that MUST keep working is the opposite one — a window first
    /// seen before its app icon was ready arrives icon-less and depends on a later event to carry it.
    /// Comparing hashes handles that: absent-then-present is a CHANGE and is sent. A client that
    /// joins later is unaffected either way, since Hello replies with the full projection.</para>
    /// </summary>
    private ForeignWindow WithoutUnchangedIcon(ForeignWindow w)
    {
        var id = w.Id.Value;
        var hash = w.IconPng is { Length: > 0 } png ? ContentHash.Fnv1a64(png) : 0L;
        if (_sentIconHash.TryGetValue(id, out var last) && last == hash)
            return hash == 0 ? w : w with { IconPng = null };
        _sentIconHash[id] = hash;
        return w;
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
            // Deliberately NOT _traySeeded: a remove proves nothing about the projection being complete.
        }
        BroadcastStamped(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: t));
    }

    private void UpsertTrayAndBroadcast(TrayItem t, CoreEventKind kind)
    {
        lock (_gate)
        {
            // Buffered add/update unlocks on replay too (bevel-wheq review); a buffered remove still does not.
            if (_seedBuffer is not null)
            {
                _seedBuffer.Add(() => { _trayById[t.Id.Value] = t; _traySeeded = true; });
                return;
            }
            _trayById[t.Id.Value] = t;
            // An add/update proves the tray stream is live (the helper is up and enumerating), so later
            // snapshots are trustworthy enough to publish; a remove alone proves nothing.
            _traySeeded = true;
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
            case CoreCommandKind.EnumerateWindowsProjected:
                // Straight off the projection the delta stream already maintains — no helper pass, and
                // the icons dropped (bevel-ougs). Under _gate so it never observes a half-applied seed.
                lock (_gate)
                    return new CoreResponse(Ok: true,
                        Windows: _windowById.Values.Select(w => w with { IconPng = null }).ToArray());
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
            case CoreCommandKind.Zoom:
                await _windows.ZoomAsync(Id(cmd), ct).ConfigureAwait(false); return CoreResponse.Success();
            case CoreCommandKind.Reposition:
                await _windows.RepositionAsync(Id(cmd),
                    cmd.Bounds ?? throw new ArgumentException("Reposition needs Bounds"), cmd.ExpectedBounds, ct)
                    .ConfigureAwait(false);
                return CoreResponse.Success();
            case CoreCommandKind.CaptureWindow:
                return new CoreResponse(Ok: true, Png: await _windows.CaptureWindowAsync(
                    Id(cmd), cmd.MaxWidth ?? 0, cmd.MaxHeight ?? 0, ct).ConfigureAwait(false));
            case CoreCommandKind.EnumerateInstalledApps:
                // SERVE THE CACHE (bevel-5erm). _installed is seeded at startup and kept current by the
                // InstalledAppsChanged subscription, which is the whole reason it exists — but this
                // handler used to re-walk the filesystem on every call, and the taskbar issues one at
                // startup. That put a full app scan on the boot path twice over, right where it delays
                // the bar filling in. Falling back to a live scan only when the cache is genuinely empty
                // keeps a pre-seed caller correct rather than handing it an empty list.
                return new CoreResponse(Ok: true, InstalledApps: _installed.Count > 0
                    ? _installed
                    : await _apps.EnumerateInstalledAppsAsync(ct).ConfigureAwait(false));
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
