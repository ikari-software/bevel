using Bevel.Pal.Abstractions;
using Bevel.UI;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Locks the cross-process BGRA icon pool (bevel-gww.6): the single-writer / many-reader,
/// lock-free, memory-mapped store. The load-bearing proof is that a SECOND pool instance opened on
/// the same path — standing in for another process — reads back exactly what the writer published,
/// straight out of the shared pages. No Avalonia here; this is a pure storage/concurrency contract.
/// </summary>
public sealed class MmfBgraPoolTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"bvpool_{Guid.NewGuid():N}.mmf");

    private static PalImage MakeImage(int w, int h, byte seed)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)((i + seed) & 0xFF);
        return new PalImage(w, h, bgra);
    }

    [Fact]
    public void RoundTrips_across_a_second_pool_instance_on_the_same_path()
    {
        var path = TempPath();
        try
        {
            var img = MakeImage(16, 16, seed: 7);
            using (var writer = MmfBgraPool.CreateOrOpen(path, slotCapacity: 8, maxBgraBytes: 64 * 1024))
            {
                Assert.True(writer.TryAdd("/Applications/Safari.app|16", img));
            }

            // Fresh instance on the SAME path == another process attaching to the shared file.
            using var reader = MmfBgraPool.CreateOrOpen(path, slotCapacity: 8, maxBgraBytes: 64 * 1024);
            Assert.True(reader.TryGet("/Applications/Safari.app|16", out var got));
            Assert.Equal(img.Width, got.Width);
            Assert.Equal(img.Height, got.Height);
            Assert.Equal(img.Bgra, got.Bgra);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_gets_its_own_copy_not_an_alias()
    {
        var path = TempPath();
        try
        {
            var img = MakeImage(8, 8, seed: 1);
            using var pool = MmfBgraPool.CreateOrOpen(path, 4, 8 * 1024);
            pool.TryAdd("k", img);

            Assert.True(pool.TryGet("k", out var a));
            Assert.True(pool.TryGet("k", out var b));
            Assert.NotSame(a.Bgra, b.Bgra); // distinct arrays
            b.Bgra[0] ^= 0xFF;              // scribbling on one must not affect the other/the pool
            Assert.True(pool.TryGet("k", out var c));
            Assert.Equal(a.Bgra, c.Bgra);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Idempotent_add_does_not_grow_the_cursor_or_corrupt()
    {
        var path = TempPath();
        try
        {
            using var pool = MmfBgraPool.CreateOrOpen(path, 8, 8 * 1024);
            var img = MakeImage(16, 16, seed: 42);

            Assert.True(pool.TryAdd("dup", img));
            Assert.Equal(1, pool.PublishedCount);
            Assert.True(pool.TryAdd("dup", img)); // re-add: still true, still one slot
            Assert.True(pool.TryAdd("dup", MakeImage(16, 16, seed: 99))); // even with different pixels
            Assert.Equal(1, pool.PublishedCount);

            // The FIRST-published pixels win (frozen entry, never mutated).
            Assert.True(pool.TryGet("dup", out var got));
            Assert.Equal(img.Bgra, got.Bgra);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Distinct_keys_including_same_path_different_size_are_independent()
    {
        var path = TempPath();
        try
        {
            using var pool = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024);
            var small = MakeImage(16, 16, seed: 3);
            var large = MakeImage(32, 32, seed: 200);

            Assert.True(pool.TryAdd("x|16", small));
            Assert.True(pool.TryAdd("x|32", large));
            Assert.Equal(2, pool.PublishedCount);

            Assert.True(pool.TryGet("x|16", out var g16));
            Assert.True(pool.TryGet("x|32", out var g32));
            Assert.Equal(16, g16.Width);
            Assert.Equal(small.Bgra, g16.Bgra);
            Assert.Equal(32, g32.Width);
            Assert.Equal(large.Bgra, g32.Bgra);

            Assert.False(pool.TryGet("x|48", out _)); // never added
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Capacity_limit_rejects_further_adds_but_keeps_existing_readable()
    {
        var path = TempPath();
        try
        {
            using var pool = MmfBgraPool.CreateOrOpen(path, slotCapacity: 3, maxBgraBytes: 8 * 1024);
            Assert.True(pool.TryAdd("a", MakeImage(8, 8, 1)));
            Assert.True(pool.TryAdd("b", MakeImage(8, 8, 2)));
            Assert.True(pool.TryAdd("c", MakeImage(8, 8, 3)));
            Assert.Equal(3, pool.PublishedCount);

            Assert.False(pool.TryAdd("d", MakeImage(8, 8, 4))); // full
            Assert.Equal(3, pool.PublishedCount);

            // Existing entries survive the rejection.
            Assert.True(pool.TryGet("a", out _));
            Assert.True(pool.TryGet("c", out _));
            Assert.False(pool.TryGet("d", out _));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Oversize_bgra_is_rejected_and_pool_stays_healthy()
    {
        var path = TempPath();
        try
        {
            using var pool = MmfBgraPool.CreateOrOpen(path, slotCapacity: 4, maxBgraBytes: 1024);
            var tooBig = new PalImage(64, 64, new byte[64 * 64 * 4]); // 16 KiB > 1 KiB ceiling

            Assert.False(pool.TryAdd("huge", tooBig));
            Assert.Equal(0, pool.PublishedCount);

            // Pool is unharmed: a well-sized add still works and reads back.
            var ok = MakeImage(8, 8, 5); // 256 bytes < 1 KiB
            Assert.True(pool.TryAdd("ok", ok));
            Assert.True(pool.TryGet("ok", out var got));
            Assert.Equal(ok.Bgra, got.Bgra);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_on_empty_or_uncreated_pool_returns_false_without_throwing()
    {
        var path = TempPath();
        try
        {
            // Nothing has ever been written here; attaching creates an empty pool.
            using var pool = MmfBgraPool.CreateOrOpen(path, 8, 8 * 1024);
            Assert.Equal(0, pool.PublishedCount);
            Assert.False(pool.TryGet("anything", out var img)); // no throw on empty pool
            Assert.Null(img); // out value is default (null) on a miss; callers must not read it
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_snapshot_sees_every_entry_the_writer_published_before_it()
    {
        var path = TempPath();
        try
        {
            const int n = 200; // "hundreds of icons" — the linear-scan working size
            using var writer = MmfBgraPool.CreateOrOpen(path, slotCapacity: n, maxBgraBytes: 4 * 1024);
            for (int i = 0; i < n; i++)
                Assert.True(writer.TryAdd($"icon-{i}", MakeImage(16, 16, (byte)i)));

            using var reader = MmfBgraPool.CreateOrOpen(path, slotCapacity: n, maxBgraBytes: 4 * 1024);
            Assert.Equal(n, reader.PublishedCount);
            for (int i = 0; i < n; i++)
            {
                Assert.True(reader.TryGet($"icon-{i}", out var got));
                Assert.Equal(MakeImage(16, 16, (byte)i).Bgra, got.Bgra);
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Geometry_mismatch_recreates_the_pool()
    {
        var path = TempPath();
        try
        {
            using (var a = MmfBgraPool.CreateOrOpen(path, slotCapacity: 4, maxBgraBytes: 1024))
                a.TryAdd("old", MakeImage(8, 8, 1));

            // Reopen with a different geometry: contents are discarded, pool is healthy.
            using var b = MmfBgraPool.CreateOrOpen(path, slotCapacity: 16, maxBgraBytes: 2048);
            Assert.Equal(0, b.PublishedCount);
            Assert.False(b.TryGet("old", out _));
            Assert.Equal(16, b.SlotCapacity);
            Assert.True(b.TryAdd("new", MakeImage(8, 8, 2)));
            Assert.True(b.TryGet("new", out _));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_before_the_writer_creates_the_pool_is_detached_and_never_creates_the_file()
    {
        var path = TempPath();
        try
        {
            // Reader opens first — the writer hasn't created the file yet. It must NOT create it
            // (a reader creating/initing could zero a pool the writer publishes a moment later).
            using var reader = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024, isWriter: false);
            Assert.False(File.Exists(path), "a reader must not create the pool file");
            Assert.False(reader.TryGet("anything", out _));   // detached → always misses
            Assert.Equal(0, reader.PublishedCount);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_cannot_publish()
    {
        var path = TempPath();
        try
        {
            using (var writer = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024)) { }   // create it
            using var reader = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024, isWriter: false);
            Assert.Throws<InvalidOperationException>(() => reader.TryAdd("k", MakeImage(8, 8, 1)));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_attaches_read_only_to_an_existing_pool_and_reads_it()
    {
        var path = TempPath();
        try
        {
            var img = MakeImage(16, 16, 3);
            using (var writer = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024))
                writer.TryAdd("k", img);

            using var reader = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024, isWriter: false);
            Assert.True(reader.TryGet("k", out var got));
            Assert.Equal(img.Bgra, got.Bgra);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reader_detaches_on_a_geometry_mismatch_rather_than_reiniting()
    {
        var path = TempPath();
        try
        {
            using (var writer = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024))
                writer.TryAdd("k", MakeImage(8, 8, 1));

            // A reader asking for a DIFFERENT geometry must detach (miss), NOT recreate/zero the
            // writer's live pool — verified by re-attaching a matching reader and still finding "k".
            using (var mismatched = MmfBgraPool.CreateOrOpen(path, 16, 2048, isWriter: false))
                Assert.False(mismatched.TryGet("k", out _));

            using var ok = MmfBgraPool.CreateOrOpen(path, 8, 64 * 1024, isWriter: false);
            Assert.True(ok.TryGet("k", out _), "the writer's pool must be intact after a mismatched reader");
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }
}
