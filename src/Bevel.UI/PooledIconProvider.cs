using System.Collections.Concurrent;
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
        _inner.IconInvalidated += (_, e) => IconInvalidated?.Invoke(this, e);
    }

    public event EventHandler? IconInvalidated;

    public async ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
    {
        if (size <= 0) size = 16;
        var key = pathOrExtension + "|" + size;

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
            lock (_writeGate)
                _pool.TryAdd(key, image);
        }
        return image;
    }
}
