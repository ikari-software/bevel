using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;
using Bevel.UI;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// A changed app icon must stop being served from the shared pool (bevel-iwju).
///
/// <para>The pool key was path+size. That was harmless while the pool was inert — every process start
/// re-rendered every icon — but since bevel-3gad gave the pool a writer, entries survive a taskbar
/// restart. That was the point (≈4 s of renders per start) and also the exposure: an app that updates
/// and changes its icon keeps the OLD bitmap, because nothing about the key can express that it
/// changed, and the pool is append-only so nothing can evict it either.</para>
///
/// <para>So the key carries the source's last-write time and a changed icon simply misses. The stat
/// that costs is on EVERY lookup, including local-cache hits, so its budget is pinned here too —
/// that was the open question in the bead, and the answer decides whether the approach is viable at
/// all.</para>
/// </summary>
public sealed class IconKeyStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "bevel-iconstamp-" + Guid.NewGuid().ToString("N")[..8]);

    public IconKeyStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Counts renders and hands back a distinguishable image each time, so a stale hit is
    /// visible as the WRONG pixels rather than merely as a missing re-render.</summary>
    private sealed class CountingProvider : IIconProvider
    {
        public int Renders;
        public byte Tint = 1;

        public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Renders);
            return ValueTask.FromResult(new PalImage(1, 1, [Tint, Tint, Tint, 255]));
        }

        public event EventHandler? IconInvalidated;
        public void RaiseInvalidated() => IconInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private (PooledIconProvider Provider, CountingProvider Inner, MmfBgraPool Pool) Build()
    {
        var inner = new CountingProvider();
        var pool = MmfBgraPool.CreateOrOpen(Path.Combine(_dir, "icons.pool"), 64, 4096, isWriter: true);
        return (new PooledIconProvider(inner, pool, isWriter: true), inner, pool);
    }

    private string App(string name)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>The core case: the app's bundle changes, so the next lookup re-renders and the caller
    /// gets the NEW artwork instead of the cached old one.</summary>
    [Fact]
    public async Task A_changed_source_re_renders_instead_of_serving_the_old_icon()
    {
        var (provider, inner, pool) = Build();
        using var _ = pool;
        var app = App("Thing.app");

        var first = await provider.GetIconAsync(app, 32);
        Assert.Equal(1, inner.Renders);
        Assert.Equal(1, first.Bgra[0]);

        // Same source: served from cache, no second render.
        await provider.GetIconAsync(app, 32);
        Assert.Equal(1, inner.Renders);

        // The app updates — the bundle directory's write time moves — and its icon changed with it.
        inner.Tint = 9;
        Directory.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddSeconds(5));

        var second = await provider.GetIconAsync(app, 32);
        Assert.Equal(2, inner.Renders);
        Assert.Equal(9, second.Bgra[0]);
    }

    /// <summary>Both shapes of source must stamp: a DIRECTORY (a macOS .app bundle) and a FILE (the
    /// usual Windows source, an .exe).
    ///
    /// <para>This is the assertion windows-latest had to make for me. File.GetLastWriteTimeUtc answers
    /// for a directory on macOS/Linux but returns the 1601 sentinel for one on Windows, so the first
    /// version of the stamp was silently 0 — and therefore constant — for every bundle-shaped source on
    /// that platform, which disables the whole staleness fix while every local test passes. A
    /// file-API behaviour that holds on the dev machine is not evidence it holds anywhere else.</para></summary>
    [Fact]
    public void Both_a_directory_and_a_file_source_stamp()
    {
        var bundle = App("Bundle.app");
        var exe = Path.Combine(_dir, "program.exe");
        File.WriteAllBytes(exe, [0]);

        Assert.NotEqual(0, PooledIconProvider.SourceStamp(bundle));
        Assert.NotEqual(0, PooledIconProvider.SourceStamp(exe));
    }

    /// <summary>...and a changed FILE source re-renders too, not just a changed bundle.</summary>
    [Fact]
    public async Task A_changed_file_source_re_renders()
    {
        var (provider, inner, pool) = Build();
        using var _ = pool;
        var exe = Path.Combine(_dir, "app.exe");
        File.WriteAllBytes(exe, [0]);

        await provider.GetIconAsync(exe, 32);
        Assert.Equal(1, inner.Renders);

        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddSeconds(5));
        await provider.GetIconAsync(exe, 32);

        Assert.Equal(2, inner.Renders);
    }

    /// <summary>An unchanged source still hits — the stamp must not defeat the cache it is protecting,
    /// which is the whole ~4 s-per-start win.</summary>
    [Fact]
    public async Task An_unchanged_source_still_hits_the_cache()
    {
        var (provider, inner, pool) = Build();
        using var _ = pool;
        var app = App("Stable.app");

        for (var i = 0; i < 20; i++)
            await provider.GetIconAsync(app, 32);

        Assert.Equal(1, inner.Renders);
    }

    /// <summary>A second process reading the SAME pool file hits what the writer published — the
    /// cross-process sharing the stamp has to leave intact.</summary>
    [Fact]
    public async Task A_reader_process_still_hits_what_the_writer_published()
    {
        var path = Path.Combine(_dir, "shared.pool");
        var app = App("Shared.app");

        var writerInner = new CountingProvider();
        using var writerPool = MmfBgraPool.CreateOrOpen(path, 64, 4096, isWriter: true);
        var writer = new PooledIconProvider(writerInner, writerPool, isWriter: true);
        await writer.GetIconAsync(app, 32);
        Assert.Equal(1, writerInner.Renders);

        var readerInner = new CountingProvider { Tint = 7 };
        using var readerPool = MmfBgraPool.CreateOrOpen(path, 64, 4096, isWriter: false);
        var reader = new PooledIconProvider(readerInner, readerPool, isWriter: false);

        var got = await reader.GetIconAsync(app, 32);

        Assert.Equal(0, readerInner.Renders);      // served from the shared pages
        Assert.Equal(1, got.Bgra[0]);              // the writer's artwork, not the reader's tint
    }

    /// <summary>File-TYPE keys (".txt" and friends) have no single source file, so they keep working
    /// exactly as before — the stamp is 0 and they cache normally.</summary>
    [Fact]
    public async Task Extension_keys_are_unaffected()
    {
        var (provider, inner, pool) = Build();
        using var _ = pool;

        await provider.GetIconAsync(".txt", 16);
        await provider.GetIconAsync(".txt", 16);

        Assert.Equal(1, inner.Renders);
        Assert.Equal(0, PooledIconProvider.SourceStamp(".txt"));
        Assert.Equal(0, PooledIconProvider.SourceStamp(Path.Combine(_dir, "does-not-exist")));
    }

    /// <summary>An IconInvalidated from the inner provider must DROP the local cache, not merely relay
    /// the news. It used to do only the relay, so a subscriber that re-asked on the event got the same
    /// stale image straight back out of this decorator.</summary>
    [Fact]
    public async Task Invalidation_drops_the_local_cache()
    {
        // A READER process: its process-local dictionary is the only cache it has, so this isolates the
        // eviction. (In the writer the same key is also in the append-only pool, which cannot evict —
        // that is precisely why the key carries a source stamp instead.)
        var inner = new CountingProvider();
        using var pool = MmfBgraPool.CreateOrOpen(Path.Combine(_dir, "reader.pool"), 64, 4096, isWriter: false);
        var provider = new PooledIconProvider(inner, pool, isWriter: false);

        await provider.GetIconAsync(".txt", 16);
        await provider.GetIconAsync(".txt", 16);
        Assert.Equal(1, inner.Renders);          // cached locally

        var relayed = 0;
        provider.IconInvalidated += (_, _) => relayed++;
        inner.RaiseInvalidated();

        await provider.GetIconAsync(".txt", 16);

        Assert.Equal(1, relayed);                // the event still reaches subscribers
        Assert.Equal(2, inner.Renders);          // ...and the stale entry is gone
    }

    /// <summary>What the stamp costs, measured: the open question in the bead was whether a per-lookup
    /// stat eats the render it saves. 400 stats stands in for a Start menu's worth of apps (343
    /// measured on a real machine); the budget is one ~11.8 ms icon render for the WHOLE set.</summary>
    [Fact]
    public void The_per_lookup_stat_costs_far_less_than_one_render()
    {
        var paths = Enumerable.Range(0, 400).Select(i => App($"App{i:D3}.app")).ToArray();

        foreach (var p in paths) PooledIconProvider.SourceStamp(p);   // warm the dentry cache

        var sw = Stopwatch.StartNew();
        foreach (var p in paths) PooledIconProvider.SourceStamp(p);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalMilliseconds < 11.8,
            $"{paths.Length} source stats took {sw.Elapsed.TotalMilliseconds:F2}ms "
            + $"({sw.Elapsed.TotalMilliseconds * 1000 / paths.Length:F1}µs each) — the budget is one "
            + "icon render, since that is what a pool hit saves");
    }
}
