using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Bevel.Core;

/// <summary>
/// Persistent shell settings per the layered JSON model (01-architecture.md CFG-01..05).
///
/// P5 (bevel-gww.5): backed by a shared SQLite DB at ~/.config/bevel/settings.db so multiple
/// shell processes (taskbar, explorer, desktop, core) can each open it and read CONCURRENTLY,
/// with one-writer safety and a monotonically-incremented <c>version</c> that lets a process
/// detect external writes (see <see cref="Version"/> / <see cref="ReloadIfChangedAsync"/>).
///
/// Storage shape is unchanged: the serialized <c>_raw</c> dictionary (the exact JSON that used to
/// be settings.json) is stored verbatim in a single-row <c>settings(json)</c> blob, so the typed
/// model + per-theme overrides round-trip byte-for-byte and unknown keys are preserved. The DB is
/// just the transport. settings.json is still written on save as a passive human-readable export
/// (mirrors this repo's beads .jsonl pattern) — the DB is the single source of truth for reads.
///
/// Live cross-process propagation is wired to the shell-core broadcast in a LATER phase; this
/// phase only makes external changes DETECTABLE (poll <see cref="ReloadIfChangedAsync"/>).
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel");

    // All (de)serialization goes through SettingsJsonContext's JsonTypeInfo overloads (bevel-gww.7):
    // reflection-free and AOT/trim-clean, while the source-gen options preserve the indented,
    // case-insensitive, skip-null formatting so the on-disk blob stays byte-compatible.

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

    public SettingsService() : this(DefaultConfigDir)
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

            _raw = json is null
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringJsonElement)
                  ?? new Dictionary<string, JsonElement>();

            _version = await WriteRowAsync(conn, JsonSerializer.Serialize(_raw, SettingsJsonContext.Default.DictionaryStringJsonElement), ct)
                .ConfigureAwait(false);
        }
        else
        {
            _raw = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringJsonElement)
                   ?? new Dictionary<string, JsonElement>();
            _version = version;
        }

        ApplyRaw();
    }

    /// <summary>Write current settings to the DB (blob + version bump) in a transaction.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(ct).ConfigureAwait(false);
            var json = SerializeRaw();
            _version = await WriteRowAsync(conn, json, ct).ConfigureAwait(false);

            // Passive export: keep a human-readable settings.json mirror (the DB is the source of
            // truth; nothing reads this file at runtime — see class summary).
            await File.WriteAllTextAsync(_configPath, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Update a single setting and persist.</summary>
    public async Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
    {
        update(_settings);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Update <paramref name="themeId"/>'s whitelisted overrides and persist.</summary>
    public async Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default)
    {
        update(ThemeOverridesFor(themeId));
        await SaveAsync(ct).ConfigureAwait(false);
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
        var (json, version) = await ReadRowAsync(conn, ct).ConfigureAwait(false);
        if (json is null || version == _version)
            return false;

        _raw = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringJsonElement)
               ?? new Dictionary<string, JsonElement>();
        _version = version;
        ApplyRaw();
        Changed?.Invoke();
        return true;
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

    /// <summary>Rebuild <c>_raw</c> from the typed model + overrides and serialize it (the blob).</summary>
    private string SerializeRaw()
    {
        _raw["themeId"] = JsonSerializer.SerializeToElement(_settings.ThemeId, SettingsJsonContext.Default.String);
        _raw["colorScheme"] = JsonSerializer.SerializeToElement(_settings.ColorScheme, SettingsJsonContext.Default.String);
        _raw["uiFontFamily"] = JsonSerializer.SerializeToElement(_settings.UiFontFamily, SettingsJsonContext.Default.String);
        _raw["shellEnabled"] = JsonSerializer.SerializeToElement(_settings.ShellEnabled, SettingsJsonContext.Default.Boolean);
        _raw["showHiddenFiles"] = JsonSerializer.SerializeToElement(_settings.ShowHiddenFiles, SettingsJsonContext.Default.Boolean);
        _raw["workAreaStrategy"] = JsonSerializer.SerializeToElement(_settings.WorkAreaStrategy.ToString(), SettingsJsonContext.Default.String);
        _raw["runAtLogin"] = JsonSerializer.SerializeToElement(_settings.RunAtLogin, SettingsJsonContext.Default.Boolean);
        _raw["taskbarButtonWidth"] = JsonSerializer.SerializeToElement(_settings.TaskbarButtonWidth, SettingsJsonContext.Default.Int32);
        _raw["taskbarStartMenuFrequentCount"] = JsonSerializer.SerializeToElement(_settings.TaskbarStartMenuFrequentCount, SettingsJsonContext.Default.Int32);
        _raw["taskbarButtonWidthMode"] = JsonSerializer.SerializeToElement(_settings.TaskbarButtonWidthMode.ToString(), SettingsJsonContext.Default.String);
        _raw["taskbarMinButtonWidth"] = JsonSerializer.SerializeToElement(_settings.TaskbarMinButtonWidth, SettingsJsonContext.Default.Int32);
        _raw["taskbarButtonSize"] = JsonSerializer.SerializeToElement(_settings.TaskbarButtonSize.ToString(), SettingsJsonContext.Default.String);
        _raw["taskbarStacks"] = JsonSerializer.SerializeToElement(_settings.TaskbarStacks, SettingsJsonContext.Default.StringArray);
        _raw["taskbarRows"] = JsonSerializer.SerializeToElement(_settings.TaskbarRows, SettingsJsonContext.Default.Int32);
        _raw["taskbarShowClock"] = JsonSerializer.SerializeToElement(_settings.TaskbarShowClock, SettingsJsonContext.Default.Boolean);
        _raw["taskbarClock24Hour"] = JsonSerializer.SerializeToElement(_settings.TaskbarClock24Hour, SettingsJsonContext.Default.Boolean);
        _raw["taskbarClockShowSeconds"] = JsonSerializer.SerializeToElement(_settings.TaskbarClockShowSeconds, SettingsJsonContext.Default.Boolean);
        _raw["taskbarClockShowDate"] = JsonSerializer.SerializeToElement(_settings.TaskbarClockShowDate, SettingsJsonContext.Default.Boolean);
        _raw["taskbarShowStart"] = JsonSerializer.SerializeToElement(_settings.TaskbarShowStart, SettingsJsonContext.Default.Boolean);
        _raw["taskbarStartLabel"] = JsonSerializer.SerializeToElement(_settings.TaskbarStartLabel, SettingsJsonContext.Default.String);
        _raw["taskbarGrouping"] = JsonSerializer.SerializeToElement(_settings.TaskbarGrouping.ToString(), SettingsJsonContext.Default.String);
        _raw["taskbarButtonLabels"] = JsonSerializer.SerializeToElement(_settings.TaskbarButtonLabels.ToString(), SettingsJsonContext.Default.String);
        _raw["taskbarMiddleClickCloses"] = JsonSerializer.SerializeToElement(_settings.TaskbarMiddleClickCloses, SettingsJsonContext.Default.Boolean);
        _raw["taskbarFontSize"] = JsonSerializer.SerializeToElement(_settings.TaskbarFontSize, SettingsJsonContext.Default.Int32);
        _raw["taskbarBackgroundColor"] = JsonSerializer.SerializeToElement(_settings.TaskbarBackgroundColor, SettingsJsonContext.Default.String);
        _raw["taskbarOpacity"] = JsonSerializer.SerializeToElement(_settings.TaskbarOpacity, SettingsJsonContext.Default.Int32);
        _raw["taskbarTrayOverflowCap"] = JsonSerializer.SerializeToElement(_settings.TaskbarTrayOverflowCap, SettingsJsonContext.Default.Int32);
        _raw["taskbarTrayIconSize"] = JsonSerializer.SerializeToElement(_settings.TaskbarTrayIconSize, SettingsJsonContext.Default.Int32);
        _raw["taskbarLocked"] = JsonSerializer.SerializeToElement(_settings.TaskbarLocked, SettingsJsonContext.Default.Boolean);
        _raw["taskbarAlwaysOnTop"] = JsonSerializer.SerializeToElement(_settings.TaskbarAlwaysOnTop, SettingsJsonContext.Default.Boolean);
        _raw["taskbarShowDesktopButton"] = JsonSerializer.SerializeToElement(_settings.TaskbarShowDesktopButton, SettingsJsonContext.Default.Boolean);
        foreach (var (id, overrides) in _themeOverrides)
            _raw[$"theme:{id}"] = JsonSerializer.SerializeToElement(overrides, SettingsJsonContext.Default.ThemeOverrides);
        return JsonSerializer.Serialize(_raw, SettingsJsonContext.Default.DictionaryStringJsonElement);
    }

    /// <summary>Project <c>_raw</c> onto the typed model + per-theme overrides (defaults fill gaps).</summary>
    private void ApplyRaw()
    {
        _settings = new BevelSettings
        {
            ThemeId = GetString("themeId") ?? "win2000",
            ColorScheme = GetString("colorScheme") ?? "",
            UiFontFamily = GetString("uiFontFamily") ?? "",
            ShellEnabled = GetBool("shellEnabled") ?? true,
            ShowHiddenFiles = GetBool("showHiddenFiles") ?? false,
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
            TaskbarRows = GetInt("taskbarRows") ?? 1,
            TaskbarShowClock = GetBool("taskbarShowClock") ?? true,
            TaskbarClock24Hour = GetBool("taskbarClock24Hour") ?? true,
            TaskbarClockShowSeconds = GetBool("taskbarClockShowSeconds") ?? false,
            TaskbarClockShowDate = GetBool("taskbarClockShowDate") ?? false,
            TaskbarShowStart = GetBool("taskbarShowStart") ?? true,
            TaskbarStartLabel = GetString("taskbarStartLabel") ?? "Start",
            // Back-compat: if the new key is absent, seed grouping from the legacy bool.
            TaskbarGrouping = Enum.TryParse<TaskbarGroupingMode>(GetString("taskbarGrouping"), out var tg)
                ? tg
                : ((GetBool("taskbarGroupWindows") ?? false) ? TaskbarGroupingMode.Always : TaskbarGroupingMode.Never),
            TaskbarButtonLabels = Enum.TryParse<TaskbarButtonLabels>(GetString("taskbarButtonLabels"), out var tbl)
                ? tbl : TaskbarButtonLabels.Auto,
            TaskbarMiddleClickCloses = GetBool("taskbarMiddleClickCloses") ?? true,
            TaskbarFontSize = GetInt("taskbarFontSize") ?? 0,
            TaskbarBackgroundColor = GetString("taskbarBackgroundColor") ?? "",
            TaskbarOpacity = GetInt("taskbarOpacity") ?? 100,
            TaskbarTrayOverflowCap = GetInt("taskbarTrayOverflowCap") ?? 8,
            TaskbarTrayIconSize = GetInt("taskbarTrayIconSize") ?? 16,
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
    /// Empty = "Windows Standard" (the default palette).</summary>
    public string ColorScheme { get; set; } = "";

    /// <summary>UI font family override (FNT-01 / bevel-9js). Empty = the theme's bundled face
    /// (Noto Sans). Any installed family name shadows <c>Bevel.Font.UI</c> shell-wide.</summary>
    public string UiFontFamily { get; set; } = "";

    public bool ShellEnabled { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }

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

    /// <summary>Start button caption. Empty string = logo only (no text), Win11-style.</summary>
    public string TaskbarStartLabel { get; set; } = "Start";

    // ── Window buttons (bevel-cust.buttons) ─────────────────────────────────────────────────────

    /// <summary>How an app's multiple windows collapse onto the taskbar. On first load, if this key is
    /// absent, it is seeded from the legacy <c>taskbarGroupWindows</c> bool for back-compat (ApplyRaw).</summary>
    public TaskbarGroupingMode TaskbarGrouping { get; set; } = TaskbarGroupingMode.Never;

    /// <summary>When a window button shows its label vs. just the icon.</summary>
    public TaskbarButtonLabels TaskbarButtonLabels { get; set; } = TaskbarButtonLabels.Auto;

    /// <summary>Middle-clicking a window button closes that window (Win7+/browser-tab convention).</summary>
    public bool TaskbarMiddleClickCloses { get; set; } = true;

    // ── Appearance (bevel-cust.appearance) ──────────────────────────────────────────────────────

    /// <summary>Taskbar font size in points; 0 = the theme baseline (11).</summary>
    public int TaskbarFontSize { get; set; }

    /// <summary>Taskbar background tint as a hex colour (e.g. <c>#2A3F5F</c>); empty = theme default.</summary>
    public string TaskbarBackgroundColor { get; set; } = "";

    /// <summary>Taskbar background opacity 20–100 (%); 100 = fully opaque. Tints the bar, not its text.</summary>
    public int TaskbarOpacity { get; set; } = 100;

    // ── System tray (bevel-cust.tray) ───────────────────────────────────────────────────────────

    /// <summary>How many mirrored tray icons show inline before the rest move under the overflow chevron.</summary>
    public int TaskbarTrayOverflowCap { get; set; } = 8;

    /// <summary>Displayed size (px) of each mirrored tray icon. Default 16 (classic).</summary>
    public int TaskbarTrayIconSize { get; set; } = 16;

    // ── Behavior (bevel-cust.behavior) ──────────────────────────────────────────────────────────

    /// <summary>Locked taskbar can't be resized (the row-resize gripper is hidden), Win-style.</summary>
    public bool TaskbarLocked { get; set; }

    /// <summary>Keep the taskbar above ordinary windows. Off drops it to the normal window level so
    /// windows can cover it (auto-hide-lite / macOS-menu-bar-like coexistence).</summary>
    public bool TaskbarAlwaysOnTop { get; set; } = true;

    /// <summary>Show a Win7-style "Show desktop" sliver at the far right that minimizes every window.</summary>
    public bool TaskbarShowDesktopButton { get; set; }

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
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.PropertyType != typeof(string[]))
                p.SetValue(this, p.GetValue(other));
        TaskbarStacks = (string[])other.TaskbarStacks.Clone();
    }
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

/// <summary>bevel-m2.10.1: taskbar button height tier (drives button, row, and bar height).</summary>
public enum TaskbarButtonSize
{
    /// <summary>Compact rows for dense strips.</summary>
    Small,

    /// <summary>Win2000 classic (default).</summary>
    Normal,

    /// <summary>Taller, touch-/readability-friendly rows.</summary>
    Large,
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
