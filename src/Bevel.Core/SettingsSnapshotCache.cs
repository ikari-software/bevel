using System.Text.Json;

namespace Bevel.Core;

/// <summary>
/// A peer process's write-through cache of the LAST settings snapshot it applied from the shell core
/// (bevel-7s9n). Peer roles (taskbar / explorer / desktop) never open <c>settings.db</c> — they get
/// their settings over the shell-core IPC — which on a cold boot usually LOSES the race with the core's
/// socket bind (the core has to boot .NET and open SQLite before it serves). Without this file the
/// taskbar painted its first frame on <see cref="BevelSettings"/> DEFAULTS (the Win2000 skin, one row)
/// and visibly re-themed a second later when the real snapshot landed; if the core never answered at
/// all it stayed that way. With it, the peer paints from the last known-good snapshot with ZERO IPC
/// and the live snapshot corrects only a real difference.
///
/// <para>Contents: one small JSON object, <c>{"version": N, "settings": {...}}</c>, where
/// <c>settings</c> is the canonical blob the core pushed (the same shape <c>settings.json</c> carries)
/// and <c>version</c> is the store version it was pushed at. It lives next to <c>settings.db</c> in the
/// per-user config dir, and is written atomically (temp file + rename) so a peer killed mid-write can
/// never leave a torn file for the next boot to read. Every peer of one shell writes the same bytes
/// (they all mirror the same core), so concurrent writers are harmless.</para>
///
/// <para>This is a CACHE of the core's state, not a second source of truth: nothing reads it but the
/// peer's own first paint, a stale copy is corrected by the first live snapshot, and hand-editing it
/// changes nothing durable (the core re-pushes the DB's state on connect). A missing or unreadable file
/// simply means "no cache" — the peer falls back to today's connect-or-defaults path.</para>
/// </summary>
public sealed class SettingsSnapshotCache
{
    /// <summary>File name inside the config dir. Named for what it is (a peer's cache), and deliberately
    /// NOT <c>settings.json</c> — that one is the core's passive export.</summary>
    public const string FileName = "peer-settings-cache.json";

    private const string VersionKey = "version";
    private const string SettingsKey = "settings";

    private readonly string _dir;
    private readonly string _path;

    /// <summary>Rooted at <paramref name="configDir"/> — the production caller passes
    /// <see cref="BevelConfigDir.Path"/>; a test MUST pass its own temp directory.</summary>
    public SettingsSnapshotCache(string configDir)
    {
        _dir = configDir;
        _path = Path.Combine(configDir, FileName);
    }

    /// <summary>Full path of the cache file (diagnostics + tests).</summary>
    public string FilePath => _path;

    /// <summary>The cached snapshot, or null when there is none — the file is missing, unreadable, torn,
    /// or does not carry a well-formed settings object. Never throws: a broken cache must degrade to
    /// "no cache", not abort the peer's boot.</summary>
    public CachedSettingsSnapshot? TryRead()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var text = File.ReadAllText(_path);
            if (!SettingsService.TryParseRaw(text, out var envelope)) return null;
            if (!envelope.TryGetValue(SettingsKey, out var settings) || settings.ValueKind != JsonValueKind.Object)
                return null;
            var version = envelope.TryGetValue(VersionKey, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;
            return new CachedSettingsSnapshot(version, settings.GetRawText());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Write-through: persist <paramref name="settingsJson"/> (the canonical blob, as pushed by
    /// the core) at store <paramref name="version"/>. Atomic via temp + rename; a failure (read-only home,
    /// full disk) is swallowed — the cache is an optimisation, and the peer already holds the state in
    /// memory. A blob that does not parse as a JSON object is refused rather than cached, so the cache can
    /// never hand a later boot something the peer itself would not have applied.</summary>
    public bool TryWrite(int version, string settingsJson)
    {
        if (!SettingsService.TryParseRaw(settingsJson, out _)) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            var envelope = new Dictionary<string, JsonElement>
            {
                [VersionKey] = JsonSerializer.SerializeToElement(version, SettingsJsonContext.Default.Int32),
                [SettingsKey] = JsonDocument.Parse(settingsJson).RootElement.Clone(),
            };
            var text = JsonSerializer.Serialize(envelope, SettingsJsonContext.Default.DictionaryStringJsonElement);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, text);
                File.Move(tmp, _path, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                try { File.Delete(tmp); } catch { /* leave nothing behind */ }
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>What <see cref="SettingsSnapshotCache.TryRead"/> hands back: the store version the blob was
/// pushed at, and the blob itself (canonical settings JSON, re-serialised — compare it to a live one with
/// <see cref="SettingsService.BlobsEquivalent"/>, never byte-for-byte).</summary>
public readonly record struct CachedSettingsSnapshot(int Version, string Json);
