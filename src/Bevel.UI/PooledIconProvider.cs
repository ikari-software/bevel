using System.Collections.Concurrent;
using System.IO;
using Bevel.Pal.Abstractions;

namespace Bevel.UI;

/// <summary>
/// An <see cref="IIconProvider"/> that fronts a real provider with the shared, memory-mapped
/// <see cref="MmfBgraPool"/> (bevel-gww.6) — this is where the pool sits "behind IIconProvider". A cold
/// icon is rendered ONCE by the inner provider and published to the pool; every later request — in this
/// process, or (once the shell is split) in a UI reader process that maps the same file — is served
/// straight from the shared BGRA pages instead of re-decoding via NSWorkspace.
///
/// <para>The pool is SINGLE-WRITER / many-reader, so exactly one process publishes: the all-in-one or
/// the shell-core owner (<c>isWriter</c>). Reader processes only ever <see cref="MmfBgraPool.TryGet"/>;
/// on a miss they render privately through the inner provider and keep the result in a process-local
/// dictionary — they must never write the shared file. Within the writer process, concurrent render
/// threads are serialized onto the pool by a lock, because the pool tolerates one writer THREAD, not
/// many, and <see cref="GetIconAsync"/> is called concurrently (each cold render runs off-thread).</para>
///
/// <para>The pool's lifetime is owned by the DI container (registered as a singleton), not by this
/// decorator — several consumers may share one pool — so this type does not dispose it.</para>
/// </summary>
public sealed class PooledIconProvider : IIconProvider
{
    private readonly IIconProvider _inner;
    private readonly MmfBgraPool _pool;
    private readonly bool _isWriter;

    // Process-local fast path: instant re-hits with no MMF scan, and the ONLY cache a reader process
    // has for its private (pool-miss) renders.
    private readonly ConcurrentDictionary<string, PalImage> _local = new();
    private readonly object _writeGate = new();

    public PooledIconProvider(IIconProvider inner, MmfBgraPool pool, bool isWriter)
    {
        _inner = inner;
        _pool = pool;
        _isWriter = isWriter;
        _inner.IconInvalidated += (_, e) =>
        {
            // Actually DROP the local cache, don't just relay the news (bevel-iwju). This handler used
            // to forward the event and nothing else, so a provider announcing "icons changed" left both
            // of this decorator's caches serving the old pixels — the subscriber re-asked and got the
            // same stale image back. The shared pool can't be evicted (append-only, lock-free publish
            // cursor), which is why the KEY carries a source stamp: a changed icon simply misses.
            _local.Clear();
            IconInvalidated?.Invoke(this, e);
        };
    }

    public event EventHandler? IconInvalidated;

    public async ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
    {
        if (size <= 0) size = 16;
        var key = pathOrExtension + "|" + size + "|" + SourceStamp(pathOrExtension);

        // 1. Process-local hit — instant and thread-safe, no pool scan.
        if (_local.TryGetValue(key, out var hit))
            return hit;

        // 2. Shared-pool hit — this or another process already decoded it into the mapped pages.
        if (_pool.TryGet(key, out var pooled))
        {
            _local[key] = pooled;
            return pooled;
        }

        // 3. Cold: render via the real provider, cache locally, and (writer only) publish to the pool.
        var image = await _inner.GetIconAsync(pathOrExtension, size, ct).ConfigureAwait(false);
        _local[key] = image;
        if (_isWriter)
        {
            // Serialize pool writes: TryAdd is idempotent and O(published), so the lock is brief; it
            // only prevents two concurrent cold renders from racing the single-writer publish cursor.
            bool published;
            lock (_writeGate)
                published = _pool.TryAdd(key, image);

            // A FULL pool returns false and keeps doing so: the store is append-only with a fixed slot
            // capacity, so from then on every process-start re-renders every icon at ~11.8ms each with
            // nothing saying why. Report it once rather than degrading in silence (bevel-web5 tracks
            // giving the pool a real eviction/compaction path; the slot budget is tight because stale
            // keys are never reclaimed).
            if (!published && !_poolFullReported)
            {
                _poolFullReported = true;
                Console.Error.WriteLine(
                    "[icons] shared icon pool is full — every later cold icon will be re-rendered per "
                    + "process instead of shared. Delete the pool file to reclaim it (bevel-web5).");
            }
        }
        return image;
    }

    private bool _poolFullReported;

    /// <summary>
    /// A cheap stamp that CHANGES when the icon's source does, so a changed icon misses instead of
    /// being served forever from a key that cannot express it (bevel-iwju).
    ///
    /// <para>The pool key was path+size. That was harmless while the pool was inert — every process
    /// start re-rendered everything — but entries now survive a writer restart, which was the point of
    /// giving the pool a writer and is also the new exposure: an app that updates and changes its icon
    /// keeps the OLD bitmap until the file is recreated. Including the bundle's last-write time means
    /// the old key is simply never asked for again.</para>
    ///
    /// <para>Returns 0 for anything that is not an existing filesystem entry — bare extensions like
    /// ".txt" (the file-TYPE icons, which have no single source file) and paths that have gone. Those
    /// keys behave exactly as before.</para>
    ///
    /// <para>Cost: one stat per lookup, including on local-cache hits, because the key is what the hit
    /// is looked up by. Measured at 1.7µs — 0.68ms for 400 paths, i.e. a whole Start menu for a fraction of one icon —
    /// against the ~11.8ms render it protects. <c>IconKeyStampTests</c> pins that budget.</para>
    /// </summary>
    internal static long SourceStamp(string pathOrExtension)
    {
        if (string.IsNullOrEmpty(pathOrExtension) || pathOrExtension[0] != Path.DirectorySeparatorChar)
            return 0;   // an extension or a relative key — nothing to stat
        try
        {
            // File.GetLastWriteTimeUtc answers for directories too (an .app bundle IS a directory, and
            // an update replaces it), and returns a sentinel rather than throwing when the path is gone.
            var t = File.GetLastWriteTimeUtc(pathOrExtension);
            return t == DateTime.FromFileTimeUtc(0) ? 0 : t.Ticks;
        }
        catch (Exception)
        {
            return 0;   // unreadable: behave as the un-stamped key did
        }
    }
}
