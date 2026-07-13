using Bevel.Pal.Abstractions;
using Bevel.UI;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Locks the behaviour of the pool-backed <see cref="IIconProvider"/> decorator (bevel-gww.6): the
/// writer renders once and publishes to the shared pool; a reader over the SAME pool file serves the
/// icon without ever calling its own inner provider (the cross-process win); and a reader miss falls
/// back to a private render without writing the shared file. Uses a counting fake inner provider so
/// "rendered once" / "never rendered" are assertions, not guesses.
/// </summary>
public sealed class PooledIconProviderTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"bvpool_{Guid.NewGuid():N}.mmf");

    /// <summary>A fake inner provider that counts renders per key and returns a distinct image each call.</summary>
    private sealed class CountingProvider : IIconProvider
    {
        private int _next = 1;
        public readonly Dictionary<string, int> Renders = new();

        public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        {
            var key = pathOrExtension + "|" + size;
            Renders[key] = Renders.GetValueOrDefault(key) + 1;
            var seed = (byte)_next++;
            var bgra = new byte[8 * 8 * 4];
            Array.Fill(bgra, seed);
            return ValueTask.FromResult(new PalImage(8, 8, bgra));
        }

        public event EventHandler? IconInvalidated { add { } remove { } }
    }

    private static MmfBgraPool Pool(string path) =>
        MmfBgraPool.CreateOrOpen(path, slotCapacity: 16, maxBgraBytes: 8 * 8 * 4);

    [Fact]
    public async Task Writer_renders_once_then_serves_from_pool()
    {
        var path = TempPath();
        try
        {
            var inner = new CountingProvider();
            using var pool = Pool(path);
            var provider = new PooledIconProvider(inner, pool, isWriter: true);

            var first = await provider.GetIconAsync("/Applications/Safari.app", 16);
            var second = await provider.GetIconAsync("/Applications/Safari.app", 16);

            Assert.Equal(1, inner.Renders["/Applications/Safari.app|16"]); // rendered exactly once
            Assert.Equal(first.Bgra, second.Bgra);                          // same pixels back
            Assert.Equal(1, pool.PublishedCount);                           // and published to the pool
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_serves_writer_published_icon_without_rendering()
    {
        var path = TempPath();
        try
        {
            // Writer process publishes the icon.
            var writerInner = new CountingProvider();
            using var writerPool = Pool(path);
            var writer = new PooledIconProvider(writerInner, writerPool, isWriter: true);
            var published = await writer.GetIconAsync("/Applications/Mail.app", 32);

            // Reader process maps the SAME pool file — a fresh inner provider that must NOT be touched.
            var readerInner = new CountingProvider();
            using var readerPool = Pool(path);
            var reader = new PooledIconProvider(readerInner, readerPool, isWriter: false);

            var got = await reader.GetIconAsync("/Applications/Mail.app", 32);

            Assert.Equal(published.Bgra, got.Bgra);   // exact pixels straight out of the shared pages
            Assert.Empty(readerInner.Renders);        // the reader never rendered — the whole point
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_miss_falls_back_to_private_render_and_does_not_publish()
    {
        var path = TempPath();
        try
        {
            var inner = new CountingProvider();
            using var pool = Pool(path);
            var reader = new PooledIconProvider(inner, pool, isWriter: false);

            var img = await reader.GetIconAsync("/Applications/Notes.app", 16);

            Assert.NotNull(img.Bgra);
            Assert.Equal(1, inner.Renders["/Applications/Notes.app|16"]); // rendered privately
            Assert.Equal(0, pool.PublishedCount);                         // reader never writes the pool

            // ...and the private render is cached locally: a second call does not re-render.
            await reader.GetIconAsync("/Applications/Notes.app", 16);
            Assert.Equal(1, inner.Renders["/Applications/Notes.app|16"]);
        }
        finally { File.Delete(path); }
    }
}
