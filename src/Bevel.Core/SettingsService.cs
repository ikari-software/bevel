using System.Text.Json;
using System.Text.Json.Serialization;
using Bevel.Core.Components;
using Microsoft.Data.Sqlite;

namespace Bevel.Core;

/// <summary>
/// Persistent shell settings per the layered JSON model (01-architecture.md CFG-01..05).
///
/// P5 (bevel-gww.5): backed by a shared SQLite DB at ~/.config/bevel/settings.db so multiple
/// shell processes (taskbar, filer, desktop, core) can each open it and read CONCURRENTLY,
/// with one-writer safety and a monotonically-incremented <c>version</c> that lets a process
/// detect external writes (see <see cref="Version"/> / <see cref="ReloadIfChangedAsync"/>).
///
/// Storage shape is unchanged: the serialized <c>_raw</c> dictionary (the exact JSON that used to
/// be settings.json) is stored verbatim in a single-row <c>settings(json)</c> blob, so the typed
/// model + per-theme overrides round-trip byte-for-byte and unknown keys are preserved. The DB is
/// just the transport. settings.json is still written on save as a passive human-readable export
/// (mirrors this repo's beads .jsonl pattern) — the DB is the single source of truth for reads.
///
/// Live cross-process propagation runs through the shell core (core-owns-settings, bevel-6nve): the
/// <c>--role=core</c> process is the SOLE opener + writer of this DB, snapshots the blob to every UI
/// process on connect, and broadcasts a fresh snapshot after each write it applies. Peer roles never
/// open the DB — they get <c>RemoteSettingsService</c> instead. <see cref="ReloadIfChangedAsync"/> and
/// the monotonic <see cref="Version"/> remain for the DB-layer tests and interface-contract compat.
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{

    // All (de)serialization goes through SettingsJsonContext's JsonTypeInfo overloads (bevel-gww.7):
    // reflection-free and AOT/trim-clean, while the source-gen options preserve the indented,
    // case-insensitive, skip-null formatting so the on-disk blob stays byte-compatible.

    /// <summary>
    /// Path handed to the projection-only instances below. They are NEVER opened — the blob pipeline is
    /// pure in-memory — so this must be somewhere that is neither the user's real config dir (which the
    /// old code borrowed, making a pure function look like it touched live state) nor a relative path
    /// that could materialise a stray file in the working directory if the code ever changed.
    /// </summary>
    private static readonly string ProjectionOnlyDir =
        Path.Combine(Path.GetTempPath(), "bevel-projection-only-never-opened");

    private readonly string _configDir;
    private readonly string _configPath; // legacy settings.json: migration source + passive export
    private readonly string _dbPath;
    private Dictionary<string, JsonElement> _raw = new();
    private BevelSettings _settings = new();
    private readonly Dictionary<string, ThemeOverrides> _themeOverrides = new();

    // Persistent connection: one per process, opened lazily on first Load/Save. Modelling "a
    // process opens the DB and keeps it open"; disposed with the service. WAL + Busy Timeout are
    // set on this connection (busy_timeout is per-connection; WAL persists in the DB header).
    private SqliteConnection? _connection;
    // Serializes writers (bevel-cust review): live-apply sliders/text boxes call UpdateAsync on every
    // tick/keystroke, so two SaveAsync calls could otherwise overlap on the passive-export
    // File.WriteAllTextAsync (a sharing-violation IOException surfacing in an async-void handler). The
    // DB writes are already sync-over-async, but the file write genuinely yields — this gate covers it.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _version; // last-loaded DB version; SaveAsync bumps it, ReloadIfChangedAsync compares

    /// <summary>Raised after <see cref="ReloadIfChangedAsync"/> pulls in an external write.</summary>
    public event Action? Changed;

    /// <summary>Production ctor: the real per-user config dir. Resolving it THROWS inside a test host
    /// (see <see cref="BevelConfigDir"/>) — a test must name its own directory via the seam below.</summary>
    public SettingsService() : this(BevelConfigDir.Path)
    {
    }

    /// <summary>Test seam: settings rooted at a custom directory (bevel-wym).</summary>
    internal SettingsService(string configDir)
    {
        _configDir = configDir;
        _configPath = Path.Combine(configDir, "settings.json");
        _dbPath = Path.Combine(configDir, "settings.db");
    }

    /// <summary>Current settings snapshot.</summary>
    public BevelSettings Current => _settings;

    /// <summary>
    /// Last-loaded DB change version (monotonic; bumped on every write). A peer process that sees a
    /// higher version in the DB knows the settings changed underneath it — poll
    /// <see cref="ReloadIfChangedAsync"/> to pick the change up. 0 until first <see cref="LoadAsync"/>.
    /// </summary>
    public int Version => _version;

    /// <summary>
    /// Whitelisted overrides for <paramref name="themeId"/> (05-theming.md §1 layer 4),
    /// created empty on first access. Persisted under a <c>theme:&lt;id&gt;</c> key.
    /// </summary>
    public ThemeOverrides ThemeOverridesFor(string themeId)
        => _themeOverrides.TryGetValue(themeId, out var o) ? o : _themeOverrides[themeId] = new ThemeOverrides();

    /// <summary>Load settings from the DB, merging with defaults; seeds the DB on first run.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_configDir);
        var conn = await OpenAsync(ct).ConfigureAwait(false);

        // ConfigureAwait(false) throughout: startup calls this as LoadAsync().GetResult() on the
        // Avalonia UI thread. With the default context-capturing await, a continuation would be
        // posted back to that blocked UI thread → deadlock (no windows, app ignores SIGTERM;
        // bevel-zd6). Microsoft.Data.Sqlite's *Async methods complete synchronously so they never
        // capture a context anyway, but ConfigureAwait(false) is kept on every await as the
        // belt-and-suspenders guard the deadlock fix demands.
        var (json, version) = await ReadRowAsync(conn, ct).ConfigureAwait(false);

        if (json is null)
        {
            // First run: no row yet. Import a legacy settings.json once if present (so users don't
            // lose settings), otherwise start from defaults — mirroring today's "missing file
            // yields defaults". Seed the single row at version 1.
            if (File.Exists(_configPath))
                json = await File.ReadAllTextAsync(_configPath, ct).ConfigureAwait(false);

            _raw = ParseRawOrDefault(json);

            _version = await WriteRowAsync(conn, JsonSerializer.Serialize(_raw, SettingsJsonContext.Default.DictionaryStringJsonElement), ct)
                .ConfigureAwait(false);
        }
        else
        {
            _raw = ParseRawOrDefault(json);
            _version = version;
        }

        MigrateRaw();   // upgrade an older-schema blob before projecting it (bevel-4er2)
        ApplyRaw();
    }

    /// <summary>
    /// Write current settings to the DB (blob + version bump) in a transaction — a whole-blob write that
    /// serializes whatever <see cref="Current"/> + overrides currently hold. Used for whole-object saves
    /// (mostly tests, and the deliberate <c>CopyFrom</c> "revert to baseline"); the per-setting delta paths
    /// (<see cref="UpdateAsync"/> / <see cref="UpdateThemeOverridesAsync"/>) go through
    /// <see cref="MutateMergeAndSaveAsync"/> instead. Under core-owns-settings (bevel-6nve) the core is the
    /// SOLE writer, so there is no concurrent peer to reconcile against.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(ct).ConfigureAwait(false);
            var json = SerializeRaw();
            _version = await WriteRowAsync(conn, json, ct).ConfigureAwait(false);
            await WritePassiveExportAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Update a single setting and persist. The delta MERGES onto the current in-memory state
    /// (see <see cref="MutateMergeAndSaveAsync"/>) — changing one key never drops the others.</summary>
    public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
        // Re-read the _settings FIELD on each apply (not a captured local) so the delta always lands on the
        // live instance, even if a reload swapped it out from under us.
        => MutateMergeAndSaveAsync(() => update(_settings), ct);

    /// <summary>Update <paramref name="themeId"/>'s whitelisted overrides and persist, merging onto the
    /// current in-memory state.</summary>
    public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default)
        // ThemeOverridesFor re-fetches from the current overrides dict on each apply, mirroring UpdateAsync.
        => MutateMergeAndSaveAsync(() => update(ThemeOverridesFor(themeId)), ct);

    /// <summary>
    /// The delta write path: apply <paramref name="applyDelta"/> to the in-memory model, serialize the whole
    /// current state, and commit it — a straight single-writer apply.
    ///
    /// <para>The delta MERGES onto the current in-memory state: <paramref name="applyDelta"/> mutates one (or
    /// a few) keys on the live <see cref="Current"/>/overrides, and <see cref="SerializeRaw"/> then captures
    /// every other explicit key alongside it, so changing key Y never drops an earlier key X. The cross-process
    /// compare-and-swap that used to reconcile two writers is GONE: under core-owns-settings (bevel-6nve) the
    /// shell core is the SOLE opener + writer of settings.db, so there is no peer to race — beads y7r4/ha3x
    /// (the CAS retry + clobber-merge) are dissolved.</para>
    ///
    /// <para><see cref="_writeLock"/> is KEPT: within this one process, live-apply sliders/text boxes call
    /// <see cref="UpdateAsync"/> on every tick/keystroke, and the lock still serialises those against each
    /// other and guards the passive-export file write from overlapping itself.</para>
    /// </summary>
    private async Task MutateMergeAndSaveAsync(Action applyDelta, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(ct).ConfigureAwait(false);
            applyDelta();                 // merge the delta onto the current in-memory state
            var json = SerializeRaw();
            // Unconditional upsert: WriteRowAsync seeds version 1 on a first write (Save-without-Load too)
            // and bumps monotonically thereafter. No CAS — the core is the only writer.
            _version = await WriteRowAsync(conn, json, ct).ConfigureAwait(false);
            await WritePassiveExportAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Passive export: a human-readable settings.json mirror (the DB is the source of truth; nothing
    /// reads this at runtime). Written ATOMICALLY via a temp file + replace: two processes' concurrent
    /// WriteAllText to the same path throw a sharing violation on Windows' exclusive locking. temp+replace makes
    /// each writer touch its own file; the replace is a fast atomic rename, and a lost race is harmless (the DB
    /// already holds the value). Callers hold <see cref="_writeLock"/>.</summary>
    private async Task WritePassiveExportAsync(string json, CancellationToken ct)
    {
        var tmp = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(tmp); } catch { /* leave nothing behind */ }
        }
    }

    /// <summary>
    /// If another process (or instance) bumped the DB <see cref="Version"/> past our last-loaded
    /// one, re-read the row, refresh <see cref="Current"/> + overrides, raise <see cref="Changed"/>
    /// and return true. Otherwise a cheap version probe returns false without touching state. This
    /// is the minimal, additive hook a poll — or the later core broadcast — uses to react to
    /// external writes.
    /// </summary>
    public async Task<bool> ReloadIfChangedAsync(CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct).ConfigureAwait(false);
        // Cheap probe first: on the common no-change tick pull ONLY the version int, not the whole
        // settings blob (every ~750ms poll across every process otherwise re-read and discarded the
        // full JSON just to compare an int; bevel-6nve).
        var version = await ReadVersionAsync(conn, ct).ConfigureAwait(false);
        if (version is null || version == _version)
            return false;

        var (json, fullVersion) = await ReadRowAsync(conn, ct).ConfigureAwait(false);
        if (json is null)
            return false;

        _raw = ParseRawOrDefault(json);
        _version = fullVersion;
        MigrateRaw();   // a peer mid-upgrade may still write an older-schema blob (bevel-4er2)
        ApplyRaw();
        Changed?.Invoke();
        return true;
    }

    // ── Wire helpers (bevel-6nve): the seam the core-owned IPC path serializes over ──────────────

    /// <summary>Serialize the current settings to the canonical persisted JSON blob — byte-identical to the
    /// blob <see cref="SaveAsync"/> writes and <see cref="LoadAsync"/> reads back — by running the same
    /// prune + serialize (<see cref="SerializeRaw"/>) the persist path uses. The core pushes this as its
    /// snapshot on the wire; no serialization is duplicated here.</summary>
    public string SnapshotJson() => SerializeRaw();

    /// <summary>
    /// Merge the changed top-level keys carried in <paramref name="patchJson"/> onto the current <c>_raw</c>
    /// state, then run the existing migrate + project + persist pipeline. This is a straight single-writer
    /// apply (the core is the sole writer, so there is no peer to CAS against). Unknown keys in the patch are
    /// preserved verbatim, and keys the patch omits keep their current value — a changed-keys MERGE, not a
    /// whole-blob replace. Reuses <see cref="MigrateRaw"/>/<see cref="ApplyRaw"/>/<see cref="SerializeRaw"/>;
    /// no serialization is duplicated.
    /// </summary>
    public async Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(ct).ConfigureAwait(false);

            // Overlay the changed keys onto current state (a torn/blank patch degrades to no-op, matching
            // ParseRawOrDefault's "malformed yields defaults" contract). RFC 7386 JSON Merge Patch
            // semantics: a key whose value is JSON null is a DELETION (revert-to-default), so a peer can
            // express "this knob went back to its default" — which prunes to an absent key — through the
            // same changed-keys patch instead of only ever adding/overwriting.
            foreach (var (key, value) in ParseRawOrDefault(patchJson))
            {
                if (value.ValueKind == JsonValueKind.Null) _raw.Remove(key);
                else _raw[key] = value;
            }

            MigrateRaw();   // a peer mid-upgrade may still carry an older-schema key set
            ApplyRaw();
            var json = SerializeRaw();
            _version = await WriteRowAsync(conn, json, ct).ConfigureAwait(false);
            await WritePassiveExportAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── DB-free blob projection / diff (bevel-6nve): the reuse points a REMOTE peer decodes core
    //    snapshots and computes its write patches through, so the peer's projection stays byte-identical
    //    to the core's WITHOUT the peer opening SQLite or re-implementing ApplyRaw/SerializeRaw. Pure
    //    in-memory: the transient instance never opens a connection (OpenAsync is only reached via
    //    Load/Save, which these never call), so the config dir it is rooted at is inert. ──────────────

    private static readonly JsonElement NullJson = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>Project a canonical settings blob into the typed model + per-theme overrides, running the
    /// SAME migrate + project pipeline a <see cref="LoadAsync"/> does, with no DB access. A remote peer
    /// decodes each core snapshot through this so its <see cref="Current"/> matches the core's exactly.</summary>
    public static (BevelSettings Settings, IReadOnlyDictionary<string, ThemeOverrides> Overrides) ProjectBlob(string? json)
    {
        var s = new SettingsService(ProjectionOnlyDir); // never opened — projection is pure in-memory
        s._raw = ParseRawOrDefault(json);
        s.MigrateRaw();
        s.ApplyRaw();
        return (s._settings, new Dictionary<string, ThemeOverrides>(s._themeOverrides));
    }

    /// <summary>
    /// <see cref="ProjectBlob"/> that REFUSES a blob it cannot parse instead of projecting defaults from
    /// it (bevel-lej1). A peer applies whatever the core pushes as the user's real settings — and the
    /// permissive <see cref="ParseRawOrDefault"/> turns an empty, torn, or otherwise malformed blob into an
    /// all-defaults model, which a peer would then apply as though the user had switched back to the
    /// Win2000 skin, one row, no overrides. A defaulted read must never masquerade as real data on a live
    /// surface: this returns false (nothing projected) for null / blank / non-object / malformed input, and
    /// the caller keeps what it has. A legitimately empty <c>{}</c> blob (a fresh install) still projects.
    /// </summary>
    public static bool TryProjectBlob(
        string? json,
        out BevelSettings settings,
        out IReadOnlyDictionary<string, ThemeOverrides> overrides)
    {
        if (!TryParseRaw(json, out var raw))
        {
            settings = null!;
            overrides = null!;
            return false;
        }
        var s = new SettingsService(ProjectionOnlyDir); // never opened — projection is pure in-memory
        s._raw = raw;
        s.MigrateRaw();
        s.ApplyRaw();
        settings = s._settings;
        overrides = new Dictionary<string, ThemeOverrides>(s._themeOverrides);
        return true;
    }

    /// <summary>True when two canonical blobs carry the SAME settings — the same top-level keys with
    /// structurally equal values (<see cref="JsonElement.DeepEquals"/>, so whitespace / indentation inside
    /// a nested <c>theme:*</c> object does not count; <see cref="ComputeMergePatch"/>'s raw-text rule would
    /// over-report those as changes). A peer compares its on-disk cache (re-serialised, so never
    /// byte-identical) to the core's first live snapshot with this, and raises <c>Changed</c> only when
    /// they actually differ. Either side failing to parse is "not equivalent".</summary>
    public static bool BlobsEquivalent(string? a, string? b)
    {
        if (!TryParseRaw(a, out var ra) || !TryParseRaw(b, out var rb)) return false;
        if (ra.Count != rb.Count) return false;
        foreach (var (key, value) in ra)
            if (!rb.TryGetValue(key, out var other) || !JsonElement.DeepEquals(value, other))
                return false;
        return true;
    }

    /// <summary>Serialize a typed model + per-theme overrides to the canonical pruned blob — reusing the
    /// exact <see cref="SerializeRaw"/> the persist path uses (no DB, no duplicated serialization).</summary>
    public static string SerializeBlob(BevelSettings settings, IReadOnlyDictionary<string, ThemeOverrides> overrides)
    {
        var s = new SettingsService(ProjectionOnlyDir);
        s._settings = settings;
        foreach (var (id, o) in overrides)
            s._themeOverrides[id] = o;
        return s.SerializeRaw();
    }

    /// <summary>Compute an RFC 7386 JSON merge patch of the top-level keys that DIFFER between two canonical
    /// blobs: an added/changed key carries its new value, a key present in <paramref name="beforeBlob"/> but
    /// gone from <paramref name="afterBlob"/> (reverted to default → pruned) carries JSON <c>null</c> so the
    /// core's <see cref="ApplyPatchJsonAsync"/> deletes it. This is the changed-keys write patch a remote peer
    /// sends the core (sole writer): scoped to what the caller actually changed, so concurrent edits to other
    /// keys still merge.</summary>
    public static string ComputeMergePatch(string beforeBlob, string afterBlob)
    {
        var before = ParseRawOrDefault(beforeBlob);
        var after = ParseRawOrDefault(afterBlob);
        var patch = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in after)
            if (!before.TryGetValue(key, out var old) || old.GetRawText() != value.GetRawText())
                patch[key] = value;                 // added or changed
        foreach (var key in before.Keys)
            if (!after.ContainsKey(key))
                patch[key] = NullJson;              // removed → RFC 7386 null = delete
        return JsonSerializer.Serialize(patch, SettingsJsonContext.Default.DictionaryStringJsonElement);
    }

    // ── SQLite plumbing ─────────────────────────────────────────────────────────────────────────

    // Single-row schema: forward-compatible (the whole settings blob lives in one TEXT column, so
    // new knobs need no migration), plus a monotonic version for external-change detection. The
    // CHECK(id=1) pins it to exactly one row.
    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS settings (" +
        "  id      INTEGER PRIMARY KEY CHECK(id = 1)," +
        "  json    TEXT NOT NULL," +
        "  version INTEGER NOT NULL);";

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (_connection is { State: System.Data.ConnectionState.Open } open)
            return open;

        // Journal Mode=WAL: concurrent readers + a single writer (readers never block the writer).
        // Busy Timeout=3s (Default Timeout is the ADO name): a second writer retries for 3s instead
        // of throwing "database is locked" immediately.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 3, // seconds; ADO command-level busy retry
        }.ToString();

        var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // WAL persists in the DB header (idempotent to re-set); busy_timeout is per-connection.
        // Commands/readers/transactions are disposed with a *synchronous* `using`: their DisposeAsync
        // completes synchronously for SQLite, and avoiding an implicit await keeps the deadlock
        // invariant (every genuine I/O await carries ConfigureAwait(false)) airtight and greppable.
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var create = conn.CreateCommand())
        {
            create.CommandText = CreateTableSql;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _connection = conn;
        return conn;
    }

    /// <summary>Reads just the monotonic version — the cheap no-change probe for the poll, so a
    /// steady-state tick never pulls the whole JSON blob.</summary>
    private static async Task<int?> ReadVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM settings WHERE id = 1;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result switch { long l => (int)l, int i => i, _ => null };
    }

    private static async Task<(string? Json, int Version)> ReadRowAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json, version FROM settings WHERE id = 1;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, 0);
        return (reader.GetString(0), reader.GetInt32(1));
    }

    // Upsert the single row and return the new version: first write (no row) seeds version 1,
    // every later write increments atomically. Wrapped in a transaction so blob+version move together.
    private static async Task<int> WriteRowAsync(SqliteConnection conn, string json, CancellationToken ct)
    {
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // ON CONFLICT: seed→version 1; subsequent writes→version+1. Handles Save-without-Load too.
        cmd.CommandText =
            "INSERT INTO settings (id, json, version) VALUES (1, $json, 1) " +
            "ON CONFLICT(id) DO UPDATE SET json = excluded.json, version = settings.version + 1 " +
            "RETURNING version;";
        cmd.Parameters.AddWithValue("$json", json);
        var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return version;
    }

    /// <summary>Rebuild <c>_raw</c> from the typed model + overrides and serialize it (the blob).
    ///
    /// <para>Only values that DIFFER from the code default are written; a value equal to its default is
    /// pruned from the blob. An absent key resolves to the default in <see cref="ApplyRaw"/>, so this is
    /// lossless — but it means a setting the user never deliberately changed keeps following the code
    /// default even when that default later improves (e.g. a corrected tray size), instead of freezing the
    /// old default in the DB. Explicit choices (which differ from the default) are always kept; unknown
    /// keys are preserved untouched.</para></summary>
    private string SerializeRaw()
    {
        var d = new BevelSettings();
        // Stamp the schema version so every saved blob advertises its shape (bevel-4er2) — but never
        // DOWNGRADE one written by a newer app (MigrateRaw already lifted _raw to at least the current
        // version on load, and left an even-newer stamp intact).
        var stored = _raw.TryGetValue(SchemaVersionKey, out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 0;
        if (stored < CurrentSchemaVersion)
            _raw[SchemaVersionKey] = JsonSerializer.SerializeToElement(CurrentSchemaVersion, SettingsJsonContext.Default.Int32);
        SetOrPrune("themeId", _settings.ThemeId, d.ThemeId, SettingsJsonContext.Default.String);
        SetOrPrune("colorScheme", _settings.ColorScheme, d.ColorScheme, SettingsJsonContext.Default.String);
        SetOrPrune("lunaColor", _settings.LunaColor, d.LunaColor, SettingsJsonContext.Default.String);
        SetOrPrune("lunaGloss", _settings.LunaGloss, d.LunaGloss, SettingsJsonContext.Default.String);
        SetOrPrune("uiFontFamily", _settings.UiFontFamily, d.UiFontFamily, SettingsJsonContext.Default.String);
        SetOrPrune("shellEnabled", _settings.ShellEnabled, d.ShellEnabled, SettingsJsonContext.Default.Boolean);
        SetOrPrune("showHiddenFiles", _settings.ShowHiddenFiles, d.ShowHiddenFiles, SettingsJsonContext.Default.Boolean);
        SetOrPrune("hideKnownExtensions", _settings.HideKnownExtensions, d.HideKnownExtensions, SettingsJsonContext.Default.Boolean);
        SetOrPrune("filerLeftPaneWidth", _settings.FilerLeftPaneWidth, d.FilerLeftPaneWidth, SettingsJsonContext.Default.Int32);
        SetOrPrune("filerFoldersOpen", _settings.FilerFoldersOpen, d.FilerFoldersOpen, SettingsJsonContext.Default.Boolean);
        SetOrPrune("defaultViewMode", _settings.DefaultViewMode, d.DefaultViewMode, SettingsJsonContext.Default.String);
        SetOrPrune("infoPaneStyle", _settings.InfoPaneStyle.ToString(), d.InfoPaneStyle.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("workAreaStrategy", _settings.WorkAreaStrategy.ToString(), d.WorkAreaStrategy.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("runAtLogin", _settings.RunAtLogin, d.RunAtLogin, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarButtonWidth", _settings.TaskbarButtonWidth, d.TaskbarButtonWidth, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarStartMenuFrequentCount", _settings.TaskbarStartMenuFrequentCount, d.TaskbarStartMenuFrequentCount, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarButtonWidthMode", _settings.TaskbarButtonWidthMode.ToString(), d.TaskbarButtonWidthMode.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("taskbarMinButtonWidth", _settings.TaskbarMinButtonWidth, d.TaskbarMinButtonWidth, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarButtonSize", _settings.TaskbarButtonSize.ToString(), d.TaskbarButtonSize.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("taskbarRows", _settings.TaskbarRows, d.TaskbarRows, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarShowClock", _settings.TaskbarShowClock, d.TaskbarShowClock, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarClock24Hour", _settings.TaskbarClock24Hour, d.TaskbarClock24Hour, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarClockShowSeconds", _settings.TaskbarClockShowSeconds, d.TaskbarClockShowSeconds, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarClockShowDate", _settings.TaskbarClockShowDate, d.TaskbarClockShowDate, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarShowStart", _settings.TaskbarShowStart, d.TaskbarShowStart, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarStartLabel", _settings.TaskbarStartLabel, d.TaskbarStartLabel, SettingsJsonContext.Default.String);
        SetOrPrune("taskbarGrouping", _settings.TaskbarGrouping.ToString(), d.TaskbarGrouping.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("taskbarButtonLabels", _settings.TaskbarButtonLabels.ToString(), d.TaskbarButtonLabels.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("taskbarMiddleClickCloses", _settings.TaskbarMiddleClickCloses, d.TaskbarMiddleClickCloses, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarReclickMinimize", _settings.TaskbarReclickMinimize.ToString(), d.TaskbarReclickMinimize.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("taskbarWindowSort", _settings.TaskbarWindowSort.ToString(), d.TaskbarWindowSort.ToString(), SettingsJsonContext.Default.String);
        SetOrPrune("windowlessAppsLast", _settings.WindowlessAppsLast, d.WindowlessAppsLast, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarFontSize", _settings.TaskbarFontSize, d.TaskbarFontSize, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarBackgroundColor", _settings.TaskbarBackgroundColor, d.TaskbarBackgroundColor, SettingsJsonContext.Default.String);
        SetOrPrune("taskbarOpacity", _settings.TaskbarOpacity, d.TaskbarOpacity, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarTrayOverflowCap", _settings.TaskbarTrayOverflowCap, d.TaskbarTrayOverflowCap, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarTrayIconSize", _settings.TaskbarTrayIconSize, d.TaskbarTrayIconSize, SettingsJsonContext.Default.Int32);
        SetOrPrune("updateFeedUrl", _settings.UpdateFeedUrl, d.UpdateFeedUrl, SettingsJsonContext.Default.String);
        SetOrPrune("updateCheckIntervalHours", _settings.UpdateCheckIntervalHours, d.UpdateCheckIntervalHours, SettingsJsonContext.Default.Int32);
        SetOrPrune("taskbarConsolidateMenuBar", _settings.TaskbarConsolidateMenuBar, d.TaskbarConsolidateMenuBar, SettingsJsonContext.Default.Boolean);
        SetOrPrune("startBadgeFullDetail", _settings.StartBadgeFullDetail, d.StartBadgeFullDetail, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarLocked", _settings.TaskbarLocked, d.TaskbarLocked, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarAlwaysOnTop", _settings.TaskbarAlwaysOnTop, d.TaskbarAlwaysOnTop, SettingsJsonContext.Default.Boolean);
        SetOrPrune("taskbarShowDesktopButton", _settings.TaskbarShowDesktopButton, d.TaskbarShowDesktopButton, SettingsJsonContext.Default.Boolean);
        // Arrays + per-theme overrides: prune when identical to the default / empty.
        if (_settings.TaskbarStacks.SequenceEqual(d.TaskbarStacks)) _raw.Remove("taskbarStacks");
        else _raw["taskbarStacks"] = JsonSerializer.SerializeToElement(_settings.TaskbarStacks, SettingsJsonContext.Default.StringArray);
        if (_settings.TaskbarComponents.Length == 0) _raw.Remove("taskbarComponents");
        else _raw["taskbarComponents"] = JsonSerializer.SerializeToElement(
            _settings.TaskbarComponents, SettingsJsonContext.Default.ComponentInstanceArray);
        foreach (var (id, overrides) in _themeOverrides)
        {
            if (overrides.CrispBevels is null) _raw.Remove($"theme:{id}");
            else _raw[$"theme:{id}"] = JsonSerializer.SerializeToElement(overrides, SettingsJsonContext.Default.ThemeOverrides);
        }
        return JsonSerializer.Serialize(_raw, SettingsJsonContext.Default.DictionaryStringJsonElement);
    }

    /// <summary>Writes <paramref name="key"/> only when it differs from the default; otherwise removes it
    /// so the blob carries just deliberate overrides (see <see cref="SerializeRaw"/>).</summary>
    private void SetOrPrune<T>(string key, T value, T dflt, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (EqualityComparer<T>.Default.Equals(value, dflt)) _raw.Remove(key);
        else _raw[key] = JsonSerializer.SerializeToElement(value, typeInfo);
    }

    // ── Schema versioning + migrations (bevel-4er2) ──────────────────────────────────────────────
    //
    // The blob is forward-compatible for ADDING keys (an absent key resolves to its default, unknown
    // keys are preserved), so a new setting needs NO migration. A migration is only for RENAMING,
    // re-shaping, or removing a stored key. Each such change bumps CurrentSchemaVersion and adds one
    // entry to Migrations. Blobs are migrated on READ (before every ApplyRaw) and the upgraded shape
    // persists on the next Save — so the DB may transiently hold an older-schema blob and every reader
    // (any process, any version) still converges, without a load-time write storm.

    /// <summary>Current settings-blob schema version. Bump ONLY when a stored key is renamed, re-shaped,
    /// or removed (adding a key needs no bump); each bump adds one <see cref="Migrations"/> entry.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string SchemaVersionKey = "schemaVersion";

    /// <summary><c>Migrations[v]</c> upgrades a version-<c>v</c> blob in place to version-<c>(v+1)</c>. A blob
    /// with no <see cref="SchemaVersionKey"/> is treated as version 0. Each guards on the presence of the
    /// old/new keys so re-running is harmless.</summary>
    private static readonly Action<Dictionary<string, JsonElement>>[] Migrations =
    {
        // 0 → 1: the grouping toggle went from a bool `taskbarGroupWindows` to the tri-state enum
        // `taskbarGrouping` (Never/WhenFull/Always). Seed the enum from the old bool, then drop it.
        raw =>
        {
            if (!raw.ContainsKey("taskbarGrouping")
                && raw.TryGetValue("taskbarGroupWindows", out var el)
                && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var mode = el.GetBoolean() ? nameof(TaskbarGroupingMode.Always) : nameof(TaskbarGroupingMode.Never);
                raw["taskbarGrouping"] = JsonSerializer.SerializeToElement(mode, SettingsJsonContext.Default.String);
            }
            raw.Remove("taskbarGroupWindows");
        },
    };

    /// <summary>Upgrades <c>_raw</c> in place from its stored schema version to
    /// <see cref="CurrentSchemaVersion"/>. A blob from a NEWER app (version &gt; current) is left untouched —
    /// we never downgrade — so forward-compat is preserved. Purely in-memory; persists on the next Save.</summary>
    private void MigrateRaw()
    {
        var from = _raw.TryGetValue(SchemaVersionKey, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;
        for (var ver = from; ver < CurrentSchemaVersion && ver < Migrations.Length; ver++)
            Migrations[ver](_raw);
        if (from < CurrentSchemaVersion)
            _raw[SchemaVersionKey] = JsonSerializer.SerializeToElement(CurrentSchemaVersion, SettingsJsonContext.Default.Int32);
    }

    /// <summary>Project <c>_raw</c> onto the typed model + per-theme overrides (defaults fill gaps).</summary>
    /// <summary>Deserialize the blob to the raw dict, falling back to defaults (empty) on a MALFORMED
    /// blob rather than throwing. A torn write or a hand-edited settings.db would otherwise throw
    /// JsonException out of LoadAsync — called via GetResult() before the UI starts — and hard-crash
    /// every process at boot; and out of the 750 ms poll on a peer's bad write (ce-review: reliability
    /// + testing). Degrading to defaults matches the "missing file yields defaults" contract.</summary>
    private static Dictionary<string, JsonElement> ParseRawOrDefault(string? json)
        => TryParseRaw(json, out var raw) ? raw : new Dictionary<string, JsonElement>();

    /// <summary>The strict parse under <see cref="ParseRawOrDefault"/>: true with the raw key→element bag
    /// only when <paramref name="json"/> is a well-formed JSON OBJECT. Null / blank / malformed / a
    /// non-object document all return false — the caller decides whether that means "defaults" (the
    /// store's missing-file contract) or "refuse" (a peer applying a live snapshot, bevel-lej1).</summary>
    internal static bool TryParseRaw(string? json, out Dictionary<string, JsonElement> raw)
    {
        raw = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringJsonElement);
            if (parsed is null) return false;
            raw = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ApplyRaw()
    {
        _settings = new BevelSettings
        {
            ThemeId = GetString("themeId") ?? "win2000",
            ColorScheme = GetString("colorScheme") ?? "",
            LunaColor = GetString("lunaColor") ?? "",
            LunaGloss = GetString("lunaGloss") ?? "",
            UiFontFamily = GetString("uiFontFamily") ?? "",
            ShellEnabled = GetBool("shellEnabled") ?? true,
            ShowHiddenFiles = GetBool("showHiddenFiles") ?? false,
            HideKnownExtensions = GetBool("hideKnownExtensions") ?? false,
            FilerLeftPaneWidth = GetInt("filerLeftPaneWidth") ?? 200,
            FilerFoldersOpen = GetBool("filerFoldersOpen") ?? false,
            DefaultViewMode = GetString("defaultViewMode") ?? "LargeIcons",
            InfoPaneStyle = Enum.TryParse<InfoPaneStyle>(GetString("infoPaneStyle"), out var ips) ? ips : InfoPaneStyle.Auto,
            WorkAreaStrategy = Enum.TryParse<WorkAreaStrategy>(GetString("workAreaStrategy"), out var was)
                ? was : WorkAreaStrategy.Nudge,
            RunAtLogin = GetBool("runAtLogin") ?? false,
            TaskbarButtonWidth = GetInt("taskbarButtonWidth") ?? 160,
            TaskbarStartMenuFrequentCount = GetInt("taskbarStartMenuFrequentCount") ?? 6,
            TaskbarButtonWidthMode = Enum.TryParse<TaskbarButtonWidthMode>(GetString("taskbarButtonWidthMode"), out var twm)
                ? twm : TaskbarButtonWidthMode.ShrinkToFit,
            TaskbarMinButtonWidth = GetInt("taskbarMinButtonWidth") ?? 80,
            TaskbarButtonSize = Enum.TryParse<TaskbarButtonSize>(GetString("taskbarButtonSize"), out var tbs)
                ? tbs : TaskbarButtonSize.Normal,
            TaskbarStacks = GetStringArray("taskbarStacks") ?? BevelSettings.DefaultStacks,
            TaskbarComponents = GetComponentInstances("taskbarComponents") ?? Array.Empty<ComponentInstance>(),
            TaskbarRows = GetInt("taskbarRows") ?? 1,
            TaskbarShowClock = GetBool("taskbarShowClock") ?? true,
            TaskbarClock24Hour = GetBool("taskbarClock24Hour") ?? true,
            TaskbarClockShowSeconds = GetBool("taskbarClockShowSeconds") ?? false,
            TaskbarClockShowDate = GetBool("taskbarClockShowDate") ?? false,
            TaskbarShowStart = GetBool("taskbarShowStart") ?? true,
            TaskbarStartLabel = GetString("taskbarStartLabel") ?? BevelSettings.DefaultStartLabel,
            // The legacy `taskbarGroupWindows` bool is folded into `taskbarGrouping` by migration 0→1
            // (bevel-4er2), so this only reads the current key.
            TaskbarGrouping = Enum.TryParse<TaskbarGroupingMode>(GetString("taskbarGrouping"), out var tg)
                ? tg : TaskbarGroupingMode.Never,
            TaskbarButtonLabels = Enum.TryParse<TaskbarButtonLabels>(GetString("taskbarButtonLabels"), out var tbl)
                ? tbl : TaskbarButtonLabels.Auto,
            TaskbarMiddleClickCloses = GetBool("taskbarMiddleClickCloses") ?? true,
            TaskbarReclickMinimize = Enum.TryParse<TaskbarReclickMinimize>(GetString("taskbarReclickMinimize"), out var trm)
                ? trm : TaskbarReclickMinimize.Click,
            TaskbarWindowSort = Enum.TryParse<TaskbarWindowSort>(GetString("taskbarWindowSort"), out var tws)
                ? tws : TaskbarWindowSort.OpenOrder,
            WindowlessAppsLast = GetBool("windowlessAppsLast") ?? false,
            TaskbarFontSize = GetInt("taskbarFontSize") ?? 0,
            TaskbarBackgroundColor = GetString("taskbarBackgroundColor") ?? "",
            TaskbarOpacity = GetInt("taskbarOpacity") ?? 100,
            TaskbarTrayOverflowCap = GetInt("taskbarTrayOverflowCap") ?? 8,
            TaskbarTrayIconSize = GetInt("taskbarTrayIconSize") ?? 16,
            UpdateFeedUrl = GetString("updateFeedUrl") ?? "",
            UpdateCheckIntervalHours = GetInt("updateCheckIntervalHours")
                ?? Updates.UpdateCheckPolicy.DefaultIntervalHours,
            TaskbarConsolidateMenuBar = GetBool("taskbarConsolidateMenuBar") ?? false,
            StartBadgeFullDetail = GetBool("startBadgeFullDetail") ?? false,
            TaskbarLocked = GetBool("taskbarLocked") ?? false,
            TaskbarAlwaysOnTop = GetBool("taskbarAlwaysOnTop") ?? true,
            TaskbarShowDesktopButton = GetBool("taskbarShowDesktopButton") ?? false,
        };

        _themeOverrides.Clear();
        foreach (var (key, el) in _raw)
        {
            if (key.StartsWith("theme:", StringComparison.Ordinal) && el.ValueKind == JsonValueKind.Object)
                _themeOverrides[key["theme:".Length..]] = el.Deserialize(SettingsJsonContext.Default.ThemeOverrides) ?? new ThemeOverrides();
        }
    }

    private string? GetString(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private bool? GetBool(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean() : null;

    private int? GetInt(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : null;

    private string[]? GetStringArray(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array
            ? el.Deserialize(SettingsJsonContext.Default.StringArray) : null;

    private ComponentInstance[]? GetComponentInstances(string key)
    {
        if (!_raw.TryGetValue(key, out var el)) return null;
        try
        {
            return el.Deserialize(SettingsJsonContext.Default.ComponentInstanceArray);
        }
        catch (JsonException)
        {
            // Corrupt or foreign-shaped value: fall back to "not migrated" rather than failing the
            // whole settings load. A broken component list must never cost the user their settings.
            return null;
        }
    }

    /// <summary>Close the shared connection (additive; existing callers that never dispose are unaffected).</summary>
    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

/// <summary>Typed settings model.</summary>
public sealed class BevelSettings
{
    public string ThemeId { get; set; } = "win2000";

    /// <summary>Win2000 colour scheme id (Classic <c>Colors/&lt;id&gt;.axaml</c>, W2K-01 / bevel-9js).
    /// Empty = "Windows Standard" (the default palette). This is the Win2000 theme's variant option;
    /// each theme contributes its own appearance options via <c>ThemeVariants</c>.</summary>
    public string ColorScheme { get; set; } = "";

    /// <summary>Luna colour variant (Blue/Silver/Black/Purple) — the Luna theme's colour axis. Empty =
    /// Blue (the reference). Combined at runtime with <see cref="LunaGloss"/> by the variant engine.</summary>
    public string LunaColor { get; set; } = "";

    /// <summary>Luna gloss variant (Hybrid/Gloss/Matte) — the Luna theme's gloss axis. Empty = Hybrid.</summary>
    public string LunaGloss { get; set; } = "";

    /// <summary>UI font family override (FNT-01 / bevel-9js). Empty = the theme's bundled face
    /// (Noto Sans). Any installed family name shadows <c>Bevel.Font.UI</c> shell-wide.</summary>
    public string UiFontFamily { get; set; } = "";

    public bool ShellEnabled { get; set; } = true;

    // ── Folder Options (Filer view/behaviour) ──────────────────────
    public bool ShowHiddenFiles { get; set; }

    /// <summary>Hide the extension of files that have one (never dotfiles/extensionless) in the listing.</summary>
    public bool HideKnownExtensions { get; set; }

    /// <summary>The view mode a newly-opened Filer window/tab starts in. Stored as a string because the
    /// <c>ViewMode</c> enum lives in Bevel.FileManager (which Bevel.Core can't reference); the file manager
    /// parses it. Default matches the historic hard-coded LargeIcons.</summary>
    public string DefaultViewMode { get; set; } = "LargeIcons";

    /// <summary>Visual style of the Filer's left info pane (folder "webview").</summary>
    public InfoPaneStyle InfoPaneStyle { get; set; } = InfoPaneStyle.Auto;

    /// <summary>Filer left-pane width in px (drag the splitter to resize). Persisted so the pane keeps its size.</summary>
    public int FilerLeftPaneWidth { get; set; } = 200;

    /// <summary>Whether the Filer left pane shows the Folders tree (true) instead of the info pane (false).</summary>
    public bool FilerFoldersOpen { get; set; }

    /// <summary>M2: work-area strategy (how the taskbar coexists with the Dock).</summary>
    public WorkAreaStrategy WorkAreaStrategy { get; set; } = WorkAreaStrategy.Nudge;

    /// <summary>M2: whether Bevel's desktop+taskbar should launch at login.</summary>
    public bool RunAtLogin { get; set; }

    /// <summary>M2: maximum width (logical px) of a taskbar window button.</summary>
    public int TaskbarButtonWidth { get; set; } = 160;

    /// <summary>How many entries the Start menu's curated left column shows (newest + most-used). Default 6.</summary>
    public int TaskbarStartMenuFrequentCount { get; set; } = 6;

    /// <summary>bevel-m2.10: how button width is chosen — shrink-to-fit (default) or fixed at the max.</summary>
    public TaskbarButtonWidthMode TaskbarButtonWidthMode { get; set; } = TaskbarButtonWidthMode.ShrinkToFit;

    /// <summary>bevel-m2.10: shrink-to-fit text floor (logical px) — buttons keep their label down to
    /// this width, then drop to icon-only below it. Default 80 (was hardcoded to half the max).</summary>
    public int TaskbarMinButtonWidth { get; set; } = 80;

    /// <summary>bevel-m2.10.1: taskbar button (and thus row/bar) height tier. Normal = Win2000 classic.</summary>
    public TaskbarButtonSize TaskbarButtonSize { get; set; } = TaskbarButtonSize.Normal;

    /// <summary>bevel-12g: folders shown as taskbar "stacks" — a tray-adjacent button whose flyout lists
    /// the folder's most-recent contents (the macOS Downloads-stack equivalent). Default: ~/Downloads.</summary>
    public string[] TaskbarStacks { get; set; } = DefaultStacks;

    internal static string[] DefaultStacks { get; } = new[]
    {
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };

    /// <summary>bevel-0ml: number of taskbar button rows (Win2000 drag-to-resize). 1 = classic single row.</summary>
    public int TaskbarRows { get; set; } = 1;

    // ── Clock (bevel-cust.clock) ────────────────────────────────────────────────────────────────

    /// <summary>Show the taskbar clock at all. Off hides the clock widget entirely.</summary>
    public bool TaskbarShowClock { get; set; } = true;

    /// <summary>24-hour (HH:mm) vs 12-hour (h:mm tt). Default 24h — Bevel's classic clock format.</summary>
    public bool TaskbarClock24Hour { get; set; } = true;

    /// <summary>Append seconds (…:ss); flips the tick cadence to 1s while on.</summary>
    public bool TaskbarClockShowSeconds { get; set; }

    /// <summary>Show the date beside the time (Win10/11-style), not only on hover.</summary>
    public bool TaskbarClockShowDate { get; set; }

    // ── Start button (bevel-cust.start) ─────────────────────────────────────────────────────────

    /// <summary>Show the Start button. Off hides it (the window strip takes the full width).</summary>
    public bool TaskbarShowStart { get; set; } = true;

    /// <summary>
    /// The shipped Start-button caption. Bevel's own word, not Microsoft's: the button is Bevel's
    /// front door, and the branding pass that dropped vendor marks from the badge
    /// (bevel-legal-branding) left the caption behind. One constant so the property default and the
    /// load fallback cannot drift — a mismatch there would make the key look explicitly-set to
    /// <c>SetOrPrune</c> and persist a value the user never chose.
    /// </summary>
    public const string DefaultStartLabel = "Bevel";

    /// <summary>Start button caption. Empty string = logo only (no text), Win11-style. The button
    /// auto-sizes to whatever is set here (bevel-6x9z), down to the theme's minimum width.</summary>
    public string TaskbarStartLabel { get; set; } = DefaultStartLabel;

    // ── Window buttons (bevel-cust.buttons) ─────────────────────────────────────────────────────

    /// <summary>How an app's multiple windows collapse onto the taskbar. On first load, if this key is
    /// absent, it is seeded from the legacy <c>taskbarGroupWindows</c> bool for back-compat (ApplyRaw).</summary>
    public TaskbarGroupingMode TaskbarGrouping { get; set; } = TaskbarGroupingMode.Never;

    /// <summary>When a window button shows its label vs. just the icon.</summary>
    public TaskbarButtonLabels TaskbarButtonLabels { get; set; } = TaskbarButtonLabels.Auto;

    /// <summary>Middle-clicking a window button closes that window (Win7+/browser-tab convention).</summary>
    public bool TaskbarMiddleClickCloses { get; set; } = true;

    /// <summary>When clicking the ACTIVE window's own button minimizes it (bevel-au94). Defaults to
    /// <see cref="TaskbarReclickMinimize.Click"/> — the classic Win2000 toggle, which is part of what the
    /// taskbar IS rather than a quirk to be modernised away; the other two modes exist for users who want
    /// a click to only ever raise. See <c>docs/design/taskbar-reclick-minimize.md</c>.</summary>
    public TaskbarReclickMinimize TaskbarReclickMinimize { get; set; } = TaskbarReclickMinimize.Click;

    /// <summary>How taskbar buttons are ordered. OpenOrder keeps the classic positional order (a window
    /// stays put — muscle memory); Name sorts by app name A→Z.</summary>
    public TaskbarWindowSort TaskbarWindowSort { get; set; } = TaskbarWindowSort.OpenOrder;

    /// <summary>Push running-but-windowless "app-presence" buttons (the dock-dot entries) to the end of
    /// the strip, after every window button — independent of, and layered on top of, the sort mode.</summary>
    public bool WindowlessAppsLast { get; set; }

    // ── Appearance (bevel-cust.appearance) ──────────────────────────────────────────────────────

    /// <summary>Taskbar font size in points; 0 = the theme baseline (11).</summary>
    public int TaskbarFontSize { get; set; }

    /// <summary>Taskbar background tint as a hex colour (e.g. <c>#2A3F5F</c>); empty = theme default.</summary>
    public string TaskbarBackgroundColor { get; set; } = "";

    /// <summary>Taskbar background opacity 20–100 (%); 100 = fully opaque. Tints the bar, not its text.</summary>
    public int TaskbarOpacity { get; set; } = 100;

    /// <summary>Show the full Bevel mark (glass cube with its inner core) on the Start button even at its
    /// small size. Off (default) uses the simplified glass cube, which stays cleaner at 20px; the core
    /// reads as a busier blob there. Ignored where the badge is Tux (Linux).</summary>
    public bool StartBadgeFullDetail { get; set; }

    // ── System tray (bevel-cust.tray) ───────────────────────────────────────────────────────────

    /// <summary>How many mirrored tray icons show inline before the rest move under the overflow chevron.</summary>
    public int TaskbarTrayOverflowCap { get; set; } = 8;

    /// <summary>Displayed size (px) of each mirrored tray icon. Default 16 (classic).</summary>
    public int TaskbarTrayIconSize { get; set; } = 16;

    /// <summary>Where to look for updates. EMPTY BY DEFAULT, and empty means disabled — a shell nobody
    /// has configured makes no network calls. There is no default URL to point at yet: publishing one
    /// depends on the site, which is gated on bevel-legal-branding.</summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>How often to ask, in hours. Clamped to 1..168 when used; the check never applies anything
    /// mid-session (UPD-01), it only notices.</summary>
    public int UpdateCheckIntervalHours { get; set; } = Updates.UpdateCheckPolicy.DefaultIntervalHours;

    /// <summary>Strategy C (bevel-7hf4): hide the real macOS menu bar and consolidate its items into
    /// Bevel's tray. Off (default) keeps today's mirror behaviour.</summary>
    public bool TaskbarConsolidateMenuBar { get; set; }

    // ── Behavior (bevel-cust.behavior) ──────────────────────────────────────────────────────────

    /// <summary>Locked taskbar can't be resized (the row-resize gripper is hidden), Win-style.</summary>
    public bool TaskbarLocked { get; set; }

    /// <summary>Keep the taskbar above ordinary windows. Off drops it to the normal window level so
    /// windows can cover it (auto-hide-lite / macOS-menu-bar-like coexistence).</summary>
    public bool TaskbarAlwaysOnTop { get; set; } = true;

    /// <summary>Show a Win7-style "Show desktop" sliver at the far right that minimizes every window.</summary>
    public bool TaskbarShowDesktopButton { get; set; }

    /// <summary>bevel-aqr7: the bar as an ordered list of component instances. Empty means "not yet
    /// migrated" — <c>TaskbarComponentsMigration</c> folds the legacy flat keys on first read.</summary>
    public ComponentInstance[] TaskbarComponents { get; set; } = Array.Empty<ComponentInstance>();

    /// <summary>A detached snapshot copy — used by the Properties dialog to revert on Cancel.</summary>
    public BevelSettings Clone()
    {
        var c = new BevelSettings();
        c.CopyFrom(this);
        return c;
    }

    /// <summary>Copies every settable scalar property from <paramref name="other"/> into this instance
    /// (deep-copying the one array), so a caller can revert live state to a snapshot without swapping the
    /// object identity the settings service relies on.</summary>
    public void CopyFrom(BevelSettings other)
    {
        foreach (var p in typeof(BevelSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                && p.PropertyType != typeof(string[]) && p.PropertyType != typeof(ComponentInstance[]))
                p.SetValue(this, p.GetValue(other));
        TaskbarStacks = (string[])other.TaskbarStacks.Clone();
        TaskbarComponents = Array.ConvertAll(other.TaskbarComponents, i => i.DeepClone());
    }
}

/// <summary>Visual style of the Filer's left info pane (the folder "webview"). Each is a native,
/// code-drawn vector template — not HTML — selectable in Folder Options.</summary>
public enum InfoPaneStyle
{
    /// <summary>No info pane (Win95-era Explorer had none).</summary>
    Off,

    /// <summary>Flat, minimal panel — Win9x "Web View" era, understated.</summary>
    Win9x,

    /// <summary>Sky-gradient banner + vector illustration + metadata/links (current default).</summary>
    Win2000,

    /// <summary>XP Luna task-pane: pastel rounded group boxes (Tasks / Other Places / Details).</summary>
    WinXP,

    /// <summary>Vista/Windows 7-inspired navigation pane: favorites, places, and contextual details.</summary>
    Modern,

    /// <summary>Match the active theme: the XP Luna task-pane under Luna, the Win2000 banner under Win2000.
    /// The default — a fresh install shows the pane that fits whatever skin it boots in. Resolved to a
    /// concrete style at apply time (never handed to the InfoPane control directly).</summary>
    Auto,
}

/// <summary>bevel-cust.buttons: how an app's multiple windows collapse onto the taskbar. (Named
/// ...Mode to avoid clashing with the <c>Bevel.Taskbar.TaskbarGrouping</c> projection helper.)</summary>
public enum TaskbarGroupingMode
{
    /// <summary>One button per window (classic Win2000 — default).</summary>
    Never,

    /// <summary>Group into one button per app only once the strip fills up (Win7/XP "combine when full").</summary>
    WhenFull,

    /// <summary>Always one button per app with a flyout list (Win7 "always combine").</summary>
    Always,
}

/// <summary>bevel-cust.buttons: how taskbar buttons are ordered.</summary>
public enum TaskbarWindowSort
{
    /// <summary>Classic positional order — a button stays where it opened (default).</summary>
    OpenOrder,

    /// <summary>By app name, A→Z.</summary>
    Name,

    /// <summary>By the icon's dominant hue (red→orange→…→violet). Yes, really — a rainbow taskbar.</summary>
    Colour,
}

/// <summary>bevel-cust.buttons: window-button label visibility.</summary>
public enum TaskbarButtonLabels
{
    /// <summary>Show labels, collapsing to icon-only as the strip fills (default shrink-to-fit).</summary>
    Auto,

    /// <summary>Always keep the label (buttons never collapse to icon-only).</summary>
    Always,

    /// <summary>Never show labels — icon-only buttons, macOS-Dock/KDE-icons-only style.</summary>
    IconOnly,
}

/// <summary>bevel-au94: what a click on the ACTIVE window's own taskbar button does.</summary>
public enum TaskbarReclickMinimize
{
    /// <summary>Classic Win2000 (default): clicking the active window's button minimizes it.</summary>
    Click,

    /// <summary>A plain click only ever raises; Option (macOS) / Alt (Windows) + click minimizes instead.</summary>
    OptionClick,

    /// <summary>Never minimize from the button — a click always raises. Minimize stays on the button's
    /// right-click menu, so the verb is never unreachable.</summary>
    Never,
}

/// <summary>bevel-m2.10.1: taskbar button height tier (drives button, row, and bar height).</summary>
public enum TaskbarButtonSize
{
    /// <summary>Compact rows for dense strips.</summary>
    Small,

    /// <summary>Win2000 classic (default).</summary>
    Normal,

    /// <summary>Taller, touch-/readability-friendly rows — and a 24px task-button glyph (bevel-c54t).</summary>
    Large,

    /// <summary>Big icons (bevel-c54t): a 40px button carrying a 32px glyph — the Win10/11 bar. Pairs with
    /// <see cref="TaskbarButtonLabels.IconOnly"/> for the icon-only look; the two settings stay
    /// independent, the Onboarding picker just selects both together.</summary>
    Big,
}

/// <summary>bevel-m2.10: taskbar button width strategy.</summary>
public enum TaskbarButtonWidthMode
{
    /// <summary>Default: buttons share the strip, shrinking as it fills (Win2000/XP behaviour).</summary>
    ShrinkToFit,

    /// <summary>Buttons stay at their max width and overflow scrolls (pre-shrink-to-fit behaviour).</summary>
    Fixed,
}

/// <summary>M2: taskbar work-area coexistence strategy.</summary>
public enum WorkAreaStrategy
{
    /// <summary>Default: Dock auto-hidden + AX repositioning of overlapping windows.</summary>
    Nudge,

    /// <summary>Opt-in strict: Dock visible at minimum size, taskbar height matches inset.</summary>
    DockShim,

    /// <summary>No mitigation; windows may underlap the taskbar.</summary>
    None,
}

/// <summary>
/// Whitelisted per-theme overrides (05-theming.md §1 layer 4), persisted under a
/// <c>theme:&lt;id&gt;</c> key so each theme keeps its own knobs (bevel-wym).
/// Null means "no override — the theme default applies".
/// </summary>
public sealed class ThemeOverrides
{
    /// <summary>Chrome spec §8: pixel-authentic hard edge bands instead of Smooth gradients.</summary>
    public bool? CrispBevels { get; set; }
}
