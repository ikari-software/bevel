using Bevel.Core;

namespace Bevel.App.ShellCore;

/// <summary>
/// The peer-role settings store (core-owns-settings, bevel-6nve): the <see cref="ISettingsService"/> an
/// Filer / Desktop / Taskbar process gets so it NEVER opens settings.db. It is a thin projection over
/// the shell-core broadcast — the core is the single opener + writer of the DB, and this caches whatever
/// the core last pushed:
///
/// <list type="bullet">
///   <item><b>Reads</b> (<see cref="Current"/> / <see cref="Version"/> / <see cref="ThemeOverridesFor"/>)
///     serve the cached projection, which is mutated ONLY from decoded core snapshots (or, before the
///     first one lands, from the on-disk copy of the last one) — never from SQLite.</item>
///   <item><b>Writes</b> (<see cref="UpdateAsync"/> / <see cref="UpdateThemeOverridesAsync"/> /
///     <see cref="SaveAsync"/>) compute a changed-keys merge patch and send it to the core as the sole
///     writer; the authoritative new state comes back as a <see cref="CoreEventKind.SettingsChanged"/>
///     broadcast (this does NOT mutate <see cref="Current"/> locally, so there is one source of truth).</item>
/// </list>
///
/// <para><b>First paint never waits on the socket (bevel-7s9n).</b> <see cref="LoadAsync"/> first reads the
/// <see cref="SettingsSnapshotCache"/> — the last snapshot this (or any sibling) peer applied, written
/// through on every apply — and projects it with zero IPC. When a cache exists the live pull runs in the
/// BACKGROUND and LoadAsync returns at once, so the taskbar's first frame is already in the persisted
/// theme; the core's on-connect snapshot is then compared to the cache and raises <see cref="Changed"/>
/// only if it actually differs (no re-template on a match). Without a cache — a first run — it falls back
/// to the bounded wait-for-core, then defaults, and the first snapshot corrects that live.</para>
///
/// <para><b>A defaulted read is never applied as real data (bevel-lej1).</b> A snapshot whose blob does
/// not parse as a JSON object is refused (logged, state kept), never projected as all-defaults — the
/// Win2000 skin flipping on by itself is exactly what that would look like. After the first live snapshot
/// the store version is monotonic: a lower-or-equal version (a duplicate on-connect push, or a stale core
/// answering a reconnect) is dropped.</para>
///
/// <para><b>One client per process (bevel-4zfs).</b> This shares the SAME <see cref="ShellCoreClient"/> as the
/// window/app/tray adapters — the old DI-keyed second <c>"settings"</c> connection existed only because the
/// core used to push its snapshot burst the moment ANY client connected, so this service's early
/// LoadAsync connect would eat the tray's snapshot before the tray adapter subscribed. The session
/// protocol deleted that race class: nothing is pushed on connect; the settings bootstrap is a
/// GetSettings PULL (request/response, not the stream), and the taskbar's Hello — sent after every
/// adapter is subscribed — is what delivers the stream snapshots. DI owns the client's disposal;
/// <see cref="Dispose"/> only detaches the event subscription, never tears it down.</para>
/// </summary>
public sealed class RemoteSettingsService : ISettingsService, IAsyncDisposable
{
    private static readonly TimeSpan CoreWait = TimeSpan.FromSeconds(5);

    private readonly ShellCoreClient _client;
    private readonly SettingsSnapshotCache? _cache;

    private readonly object _gate = new();
    private BevelSettings _current = new();
    private readonly Dictionary<string, ThemeOverrides> _themeOverrides = new();
    private int _version;       // 0 until a snapshot (cached or live) lands (matches the real service's contract)
    private string? _blob;      // the canonical blob Current was projected from; null = defaults, nothing applied
    private bool _live;         // true once a snapshot from the CORE (not the cache) has been applied
    private long _applySeq;     // orders write-through cache writes by apply order, not by store version

    private readonly object _cacheGate = new();
    private long _cachedSeq;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Constructor subscription: the live snapshots arrive on the stream thread (Hello's burst,
    /// epoch re-pushes) AND on a link restore — see <see cref="OnLinkRestored"/>.</summary>
    public RemoteSettingsService(ShellCoreClient client, SettingsSnapshotCache? cache = null)
    {
        _client = client;
        _cache = cache;
        // Arm the subscription in the ctor — BEFORE any session start — so the Hello burst's
        // SettingsSnapshot is never missed (same discipline the window/app adapters follow).
        _client.EventReceived += OnCoreEvent;
        // A settings peer never starts a SESSION (that's a window/tray concern), so a reconnect has no
        // auto re-Hello to deliver a fresh burst — re-PULL on every link restore instead (bevel-4zfs).
        _client.ConnectionChanged += OnLinkRestored;
    }

    /// <summary>Link restored → re-pull the blob in the background (bevel-4zfs). The version guard in
    /// <see cref="ApplySnapshot"/> dedups this against an in-flight GetSettings and drops stale answers;
    /// a failure is just the next retry — the reconnect supervisor owns the link itself.</summary>
    private void OnLinkRestored(object? _, bool connected)
    {
        if (connected) _ = PullLiveSnapshotInBackgroundAsync();
    }

    /// <param name="client">The process's ONE shell-core client (shared with the window/app/tray adapters
    /// where the role has them — see the type docs for why sharing is safe now).</param>
    /// <param name="cache">The on-disk snapshot cache the first paint reads and every apply writes through
    /// to; null disables caching (the pre-cache connect-or-defaults behaviour, used by a few tests).</param>

    /// <inheritdoc />
    public BevelSettings Current { get { lock (_gate) return _current; } }

    /// <inheritdoc />
    public int Version { get { lock (_gate) return _version; } }

    /// <summary>True once a snapshot from the core itself has been applied this session — i.e.
    /// <see cref="Current"/> is no longer the cached or default seed. Diagnostics + tests.</summary>
    public bool HasLiveSnapshot { get { lock (_gate) return _live; } }

    /// <inheritdoc />
    public ThemeOverrides ThemeOverridesFor(string themeId)
    {
        lock (_gate)
            return _themeOverrides.TryGetValue(themeId, out var o) ? o : _themeOverrides[themeId] = new ThemeOverrides();
    }

    /// <summary>Seeds <see cref="Current"/> for the first paint. With a cached snapshot: project it (zero
    /// IPC), kick the live pull off in the background, and return immediately — no paint waits on a
    /// socket. Without one: connect and pull the blob from the core (bounded to 5s), and on any failure
    /// (core not up yet, timeout) seed defaults and leave the subscription armed, so the on-connect
    /// snapshot corrects <see cref="Current"/> when the link is established — never opening the DB.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_cache?.TryRead() is { } cached && ApplySnapshot(cached.Json, cached.Version, fromCache: true))
        {
            // Painting from the cache. The live snapshot arrives on its own (on-connect push) — the pull
            // here only bootstraps the connection and covers a peer that dialed before the push; nothing
            // awaits it, and a failure is the same non-terminal first-connect race as below: Current is
            // already the cached state, the reconnect supervisor keeps dialing, and the core's on-connect
            // push lands whenever the link comes up. Swallowed so it can never surface as an unobserved
            // task exception.
            _ = PullLiveSnapshotInBackgroundAsync();
            return;
        }

        try
        {
            await PullLiveSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Core not up yet (a first-connect race) → seed defaults for the immediate first paint. This is
            // NOT terminal: EnsureConnectedAsync armed the client's reconnect supervisor, so once the core
            // binds its socket the on-connect SettingsSnapshot arrives and ApplySnapshot re-themes live.
            ct.ThrowIfCancellationRequested(); // the CALLER cancelled → propagate; our 5s bound / a dead core → fall back
            SeedDefaultsIfUnseeded();
        }
    }

    /// <summary>Connect and pull the current blob + version (bounded to <see cref="CoreWait"/>). Throws on
    /// a failed connect / timeout — the caller decides whether that is a fallback or a no-op.</summary>
    private async Task PullLiveSnapshotAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CoreWait);
        await _client.EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
        var resp = await _client.SendAsync(new CoreCommand(CoreCommandKind.GetSettings), cts.Token).ConfigureAwait(false);
        if (resp.Ok && resp.SettingsJson is not null)
            ApplySnapshot(resp.SettingsJson, resp.SettingsVersion ?? 0, fromCache: false);
    }

    private async Task PullLiveSnapshotInBackgroundAsync()
    {
        try { await PullLiveSnapshotAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* see LoadAsync: the on-connect push corrects state once the link is up */ }
    }

    /// <summary>Apply a delta to a CLONE, diff it against current to a changed-keys merge patch, and send it
    /// to the core (sole writer). <see cref="Current"/> is NOT mutated here — the authoritative result arrives
    /// as a <see cref="CoreEventKind.SettingsChanged"/> broadcast.</summary>
    public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
    {
        BevelSettings before;
        IReadOnlyDictionary<string, ThemeOverrides> overrides;
        lock (_gate)
        {
            before = _current;
            overrides = CloneOverrides(_themeOverrides);
        }
        var after = before.Clone();
        update(after);
        // Overrides are unchanged by this path, so passing the same set for both sides cancels them in the diff.
        var patch = SettingsService.ComputeMergePatch(
            SettingsService.SerializeBlob(before, overrides),
            SettingsService.SerializeBlob(after, overrides));
        return SendPatchAsync(patch, ct);
    }

    /// <inheritdoc />
    public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default)
    {
        BevelSettings current;
        Dictionary<string, ThemeOverrides> before;
        lock (_gate)
        {
            current = _current;
            before = CloneOverrides(_themeOverrides);
        }
        var after = CloneOverrides(before);
        var target = after.TryGetValue(themeId, out var o) ? o : after[themeId] = new ThemeOverrides();
        update(target);
        var patch = SettingsService.ComputeMergePatch(
            SettingsService.SerializeBlob(current, before),
            SettingsService.SerializeBlob(current, after));
        return SendPatchAsync(patch, ct);
    }

    /// <summary>Whole-object last-writer-wins: send the entire current canonical blob as one patch. The core
    /// overlays every explicit key (merge semantics), so this replaces the deliberate settings without a
    /// per-key diff — the peer equivalent of the real service's whole-blob <c>SaveAsync</c>.</summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        string patch;
        lock (_gate)
            patch = SettingsService.SerializeBlob(_current, CloneOverrides(_themeOverrides));
        return SendPatchAsync(patch, ct);
    }

    /// <summary>No-op for a peer: there is no DB to poll — external writes arrive as core broadcasts, not
    /// via a version probe. Returns false so the (retiring) poll loop never reports a change here.</summary>
    public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Serialize the cached projection to the canonical blob (the same shape the core snapshots).</summary>
    public string SnapshotJson()
    {
        lock (_gate)
            return SettingsService.SerializeBlob(_current, CloneOverrides(_themeOverrides));
    }

    /// <summary>Forward a raw patch to the core (sole writer) — the peer cannot persist locally, so
    /// "apply this patch" means "ask the core to apply it"; the result returns as a broadcast.</summary>
    public Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default) => SendPatchAsync(patchJson, ct);

    // ── internals ────────────────────────────────────────────────────────────────────────────────

    private async Task SendPatchAsync(string patchJson, CancellationToken ct)
    {
        var resp = await _client.SendAsync(
            new CoreCommand(CoreCommandKind.ApplySettingsUpdate, SettingsPatchJson: patchJson), ct).ConfigureAwait(false);
        if (!resp.Ok)
            throw new InvalidOperationException($"shell-core rejected the settings update: {resp.Error}");
    }

    private void OnCoreEvent(CoreEvent evt)
    {
        if (evt.Kind is CoreEventKind.SettingsSnapshot or CoreEventKind.SettingsChanged)
            ApplySnapshot(evt.SettingsJson, evt.SettingsVersion ?? 0, fromCache: false);
    }

    /// <summary>
    /// Swap in a decoded snapshot and raise <see cref="Changed"/> (outside the lock) if it changed what
    /// <see cref="Current"/> projects. Returns true when the snapshot was accepted into state (applied, or
    /// silently adopted as identical), false when it was refused or dropped. Rules, in order:
    /// <list type="number">
    ///   <item>A blob that does not parse as a JSON object is REFUSED — never projected as defaults (bevel-lej1).</item>
    ///   <item>A cached blob seeds state only before any live snapshot; it never raises Changed (it IS the
    ///     first paint, there is nothing to change from).</item>
    ///   <item>The first LIVE snapshot always wins over the cache — by content: if it is equivalent, its
    ///     version is adopted with NO Changed (the cache was right, nothing re-templates); if not, it applies
    ///     and Changed fires once (the cache was stale). Its version is not compared to the cache's, so a
    ///     replaced or reset store still corrects a peer holding a higher cached version.</item>
    ///   <item>Later live snapshots must carry a HIGHER store version — dedups the on-connect push against
    ///     the GetSettings reply, and drops a stale core's answer to a reconnect.</item>
    /// </list>
    /// Every accepted live snapshot is written through to the cache in apply order.
    /// </summary>
    private bool ApplySnapshot(string? json, int version, bool fromCache)
    {
        if (!SettingsService.TryProjectBlob(json, out var settings, out var overrides))
        {
            if (!fromCache)
                Console.Error.WriteLine(
                    $"[settings] refused a shell-core snapshot (version {version}) that is not a settings object — keeping the current state");
            return false;
        }

        bool changed;
        long seq;
        lock (_gate)
        {
            if (fromCache)
            {
                if (_blob is not null) return true; // a live (or earlier cached) snapshot already seeded state
                Project(settings, overrides, json!, version);
                return true;
            }

            if (_live && version <= _version) return false; // duplicate / stale live snapshot

            if (!_live && _blob is not null && SettingsService.BlobsEquivalent(_blob, json))
            {
                // The cache was right: adopt the live version silently. Nothing re-projects, nothing re-templates.
                _version = version;
                changed = false;
            }
            else
            {
                Project(settings, overrides, json!, version);
                changed = true;
            }
            _live = true;
            seq = ++_applySeq;
        }

        WriteThrough(seq, version, json!);
        if (changed) Changed?.Invoke();
        return true;
    }

    /// <summary>Under <see cref="_gate"/>: replace the projection.</summary>
    private void Project(BevelSettings settings, IReadOnlyDictionary<string, ThemeOverrides> overrides, string json, int version)
    {
        _current = settings;
        _themeOverrides.Clear();
        foreach (var (id, o) in overrides)
            _themeOverrides[id] = o;
        _version = version;
        _blob = json;
    }

    /// <summary>Persist an accepted live snapshot to the cache, in APPLY order: two applies racing on
    /// different threads (the on-connect push on the transport thread, the GetSettings reply on the
    /// load thread) must not let the older one land last on disk. Runs on the calling thread — the
    /// transport receive loop or the startup load, never the UI thread — and is a few hundred bytes
    /// through an atomic rename.</summary>
    private void WriteThrough(long seq, int version, string json)
    {
        if (_cache is null) return;
        lock (_cacheGate)
        {
            if (seq < _cachedSeq) return;
            _cachedSeq = seq;
            _cache.TryWrite(version, json);
        }
    }

    /// <summary>Core unreachable at load and no cache: hold defaults but leave <see cref="_blob"/> null and
    /// <see cref="_live"/> false so the next snapshot (on reconnect) still applies. No Changed is raised —
    /// nothing meaningful has been projected.</summary>
    private void SeedDefaultsIfUnseeded()
    {
        lock (_gate)
        {
            if (_blob is not null) return;
            _current = new BevelSettings();
            _themeOverrides.Clear();
            _version = 0;
        }
    }

    private static Dictionary<string, ThemeOverrides> CloneOverrides(IReadOnlyDictionary<string, ThemeOverrides> src)
    {
        var d = new Dictionary<string, ThemeOverrides>(src.Count);
        foreach (var (id, o) in src)
            d[id] = new ThemeOverrides { CrispBevels = o.CrispBevels };
        return d;
    }

    private int _disposed;

    /// <summary>Detach from the shared client's event stream (idempotent). Does NOT dispose the client —
    /// it is owned by the process's composition and shared with the window/app adapters.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.EventReceived -= OnCoreEvent;
        _client.ConnectionChanged -= OnLinkRestored;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
