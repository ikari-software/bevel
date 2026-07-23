using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Bevel.Taskbar;

/// <summary>
/// Persistent per-app usage stats for the Start menu's curated left column: how many times each app has
/// been launched, and when it was first seen (so newly-installed apps can surface on top). Backed by a
/// small JSON file in the Bevel config dir (~/.config/bevel/program-usage.json). A missing or corrupt
/// file degrades to an empty store — usage stats are a nicety, never load-bearing.
///
/// <para>Mutators persist synchronously (so a value is durable the moment it's set). Some callers run on
/// the UI thread — notably the Start-menu reconcile, which calls <see cref="MarkSeen"/> once per app.
/// Wrap a bulk mutation in <see cref="BeginBatch"/> so it collapses to a single write on scope exit
/// instead of rewriting the whole file once per app on the UI thread.</para>
/// </summary>
public sealed class ProgramUsageStore
{
    private sealed class Entry
    {
        public int LaunchCount { get; set; }
        public DateTime FirstSeenUtc { get; set; }
    }

    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel");

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byApp;
    private int _batchDepth;        // >0 while a bulk mutation is in progress; guarded by _gate
    private bool _dirtyInBatch;     // a mutation was deferred during the batch and must flush on exit

    public ProgramUsageStore(string? configDir = null)
    {
        _path = Path.Combine(configDir ?? DefaultConfigDir, "program-usage.json");
        _byApp = Load(_path);
    }

    /// <summary>Launches recorded for an app (0 if never launched/seen).</summary>
    public int LaunchCount(string appId)
    {
        lock (_gate) return _byApp.TryGetValue(appId, out var e) ? e.LaunchCount : 0;
    }

    /// <summary>When the app was first seen; <see cref="DateTime.MaxValue"/> if unknown (sorts as newest).</summary>
    public DateTime FirstSeen(string appId)
    {
        lock (_gate) return _byApp.TryGetValue(appId, out var e) ? e.FirstSeenUtc : DateTime.MaxValue;
    }

    /// <summary>Stamps first-seen for an app the first time it's encountered. Returns true if this is a
    /// brand-new app (not previously recorded) — the caller may surface it near the top.</summary>
    public bool MarkSeen(string appId, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_byApp.ContainsKey(appId)) return false;
            _byApp[appId] = new Entry { LaunchCount = 0, FirstSeenUtc = nowUtc };
            Save();
            return true;
        }
    }

    /// <summary>Records one launch of an app, creating its entry if needed. Persists.</summary>
    public void RecordLaunch(string appId)
    {
        lock (_gate)
        {
            if (!_byApp.TryGetValue(appId, out var e))
                _byApp[appId] = e = new Entry { FirstSeenUtc = DateTime.UtcNow };
            e.LaunchCount++;
            Save();
        }
    }

    /// <summary>Directly sets an app's stats (seed/import; used by tests). Persists.</summary>
    public void Set(string appId, int launchCount, DateTime firstSeenUtc)
    {
        lock (_gate)
        {
            _byApp[appId] = new Entry { LaunchCount = launchCount, FirstSeenUtc = firstSeenUtc };
            Save();
        }
    }

    /// <summary>Coalesces the writes of a bulk mutation (e.g. the Start-menu reconcile marking every app
    /// seen) into a single file write when the returned scope is disposed, instead of one write per
    /// mutation. Re-entrant. Dispose on the same thread that opened it.</summary>
    public IDisposable BeginBatch()
    {
        lock (_gate) _batchDepth++;
        return new BatchScope(this);
    }

    private void EndBatch()
    {
        lock (_gate)
        {
            if (--_batchDepth > 0) return;      // still inside an outer batch
            if (!_dirtyInBatch) return;
            _dirtyInBatch = false;
            WriteToDisk();
        }
    }

    // Called while holding _gate. Defers to a single flush while a batch is open.
    private void Save()
    {
        if (_batchDepth > 0) { _dirtyInBatch = true; return; }
        WriteToDisk();
    }

    private void WriteToDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_byApp));
        }
        catch { /* best-effort: usage stats must never break the taskbar */ }
    }

    private sealed class BatchScope : IDisposable
    {
        private ProgramUsageStore? _owner;
        public BatchScope(ProgramUsageStore owner) => _owner = owner;
        public void Dispose() { var o = _owner; _owner = null; o?.EndBatch(); }   // idempotent
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path))
                       ?? new Dictionary<string, Entry>();
        }
        catch { /* missing or corrupt → empty */ }
        return new Dictionary<string, Entry>();
    }
}
