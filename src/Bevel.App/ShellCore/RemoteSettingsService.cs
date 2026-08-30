using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App.ShellCore;

/// <summary>
/// The peer-role settings store (core-owns-settings, bevel-6nve): the <see cref="ISettingsService"/> an
/// Explorer / Desktop process gets so it NEVER opens settings.db. It is a thin projection over the
/// shell-core broadcast — the core is the single opener + writer of the DB, and this caches whatever the
/// core last pushed:
///
/// <list type="bullet">
///   <item><b>Reads</b> (<see cref="Current"/> / <see cref="Version"/> / <see cref="ThemeOverridesFor"/>)
///     serve the cached projection, which is mutated ONLY from decoded core snapshots — never from SQLite.</item>
///   <item><b>Writes</b> (<see cref="UpdateAsync"/> / <see cref="UpdateThemeOverridesAsync"/> /
///     <see cref="SaveAsync"/>) compute a changed-keys merge patch and send it to the core as the sole
///     writer; the authoritative new state comes back as a <see cref="CoreEventKind.SettingsChanged"/>
///     broadcast (this does NOT mutate <see cref="Current"/> locally, so there is one source of truth).</item>
/// </list>
///
/// <para>Startup fallback (locked decision): if the core is unreachable when <see cref="LoadAsync"/> runs,
/// it seeds <see cref="BevelSettings"/> defaults and keeps the snapshot subscription armed, so the
/// on-connect <see cref="CoreEventKind.SettingsSnapshot"/> corrects it the moment the link comes up.</para>
///
/// <para>This gets its OWN <see cref="ShellCoreClient"/> (the DI-keyed <c>"settings"</c> client), distinct
/// from the window-manager / app-environment / tray adapters' client in a split taskbar. That separation is
/// deliberate: this service connects during startup <see cref="LoadAsync"/>, and if it shared the tray
/// client that early connect would consume the core's on-connect tray snapshot before the tray adapter
/// subscribes (the tray has no reconcile backstop), leaving the split taskbar's tray empty. DI owns the
/// keyed client's disposal; <see cref="Dispose"/> only detaches the event subscription, never tears it down.</para>
/// </summary>
public sealed class RemoteSettingsService : ISettingsService, IAsyncDisposable
{
    private readonly ShellCoreClient _client;

    private readonly object _gate = new();
    private BevelSettings _current = new();
    private readonly Dictionary<string, ThemeOverrides> _themeOverrides = new();
    private int _version;      // 0 until the first snapshot lands (matches the real service's contract)
    private bool _seeded;      // distinguishes "never applied a snapshot" from "at version 0"

    /// <inheritdoc />
    public event Action? Changed;

    public RemoteSettingsService([FromKeyedServices("settings")] ShellCoreClient client)
    {
        _client = client;
        // Arm the subscription in the ctor — BEFORE any connect — so the core's on-connect
        // SettingsSnapshot is never missed (same discipline the window/app adapters follow).
        _client.EventReceived += OnCoreEvent;
    }

    /// <inheritdoc />
    public BevelSettings Current { get { lock (_gate) return _current; } }

    /// <inheritdoc />
    public int Version { get { lock (_gate) return _version; } }

    /// <inheritdoc />
    public ThemeOverrides ThemeOverridesFor(string themeId)
    {
        lock (_gate)
            return _themeOverrides.TryGetValue(themeId, out var o) ? o : _themeOverrides[themeId] = new ThemeOverrides();
    }

    /// <summary>Connects and pulls the current settings blob from the core (bounded to 5s). On any failure
    /// (core not up yet, timeout) it seeds defaults and leaves the subscription armed, so the on-connect
    /// snapshot corrects <see cref="Current"/> when the link is established — never opening the DB.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await _client.EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
            var resp = await _client.SendAsync(new CoreCommand(CoreCommandKind.GetSettings), cts.Token).ConfigureAwait(false);
            if (resp.Ok && resp.SettingsJson is not null)
                ApplySnapshot(resp.SettingsJson, resp.SettingsVersion ?? 0);
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
            ApplySnapshot(evt.SettingsJson, evt.SettingsVersion ?? 0);
    }

    /// <summary>Swap in a decoded core snapshot if it is newer than what we hold (dedup by version — the
    /// on-connect snapshot and a same-version GetSettings reply must not double-fire Changed), then raise
    /// <see cref="Changed"/> outside the lock.</summary>
    private void ApplySnapshot(string? json, int version)
    {
        if (json is null) return;
        lock (_gate)
        {
            if (_seeded && version <= _version) return;
            var (settings, overrides) = SettingsService.ProjectBlob(json);
            _current = settings;
            _themeOverrides.Clear();
            foreach (var (id, o) in overrides)
                _themeOverrides[id] = o;
            _version = version;
            _seeded = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Core unreachable at load: hold defaults but leave <see cref="_seeded"/> false so the next
    /// snapshot (on reconnect) still applies. No Changed is raised — nothing meaningful has been projected.</summary>
    private void SeedDefaultsIfUnseeded()
    {
        lock (_gate)
        {
            if (_seeded) return;
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
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
