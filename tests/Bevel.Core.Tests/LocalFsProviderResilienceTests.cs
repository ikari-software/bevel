using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// Resilience/correctness tests for LocalFsProvider: TOCTOU during enumeration
/// (bevel-2xt), consistent type descriptions across node kinds (bevel-5ty), and
/// directory-watcher disposal safety (bevel-a2s).
/// </summary>
public sealed class LocalFsProviderResilienceTests : IDisposable
{
    private readonly LocalFsProvider _provider = new();
    private readonly string _dir;

    public LocalFsProviderResilienceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-res-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private async Task<List<IVfsNode>> EnumerateAsync()
    {
        var nodes = new List<IVfsNode>();
        await foreach (var n in _provider.EnumerateAsync(
                           new VfsPath("file", _dir), new EnumerateOptions { IncludeHidden = true }, CancellationToken.None))
            nodes.Add(n);
        return nodes;
    }

    [Fact]
    public async Task Enumerate_skips_entries_that_vanish_between_listing_and_stat()
    {
        // Reproduce the TOCTOU deterministically: the directory is listed, then entries
        // are deleted while enumeration is still in flight (from the Filter callback, on
        // the first surviving entry), so the already-listed sibling names throw
        // FileNotFoundException when LocalFsProvider stats them. The guard must skip the
        // vanished entries and still return the survivor instead of aborting the listing.
        var names = new[] { "a.txt", "b.txt", "c.txt" };
        foreach (var n in names)
            await File.WriteAllTextAsync(Path.Combine(_dir, n), "x");

        var deletedSiblings = false;
        var options = new EnumerateOptions
        {
            IncludeHidden = true,
            Filter = node =>
            {
                if (!deletedSiblings)
                {
                    deletedSiblings = true;
                    foreach (var n in names)
                    {
                        var p = Path.Combine(_dir, n);
                        if (n != node.DisplayName && File.Exists(p))
                            File.Delete(p);
                    }
                }
                return true;
            },
        };

        var nodes = new List<IVfsNode>();
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var node in _provider.EnumerateAsync(new VfsPath("file", _dir), options, CancellationToken.None))
                nodes.Add(node);
        });

        Assert.Null(ex);          // TOCTOU must not abort the whole listing
        Assert.Single(nodes);     // only the first-visited entry survived the deletion
    }

    [Fact]
    public async Task TypeDescription_is_consistent_between_enumerate_and_resolve()
    {
        // bevel-5ty: the two node implementations (lazy enumerate node vs resolved node)
        // must report the SAME type label for the same extension — one shared mapping.
        var file = Path.Combine(_dir, "report.pdf");
        await File.WriteAllTextAsync(file, "x");

        var enumerated = (await EnumerateAsync()).Single(n => n.DisplayName == "report.pdf");
        var resolved = await _provider.ResolveAsync(new VfsPath("file", file), CancellationToken.None);

        Assert.Equal("PDF Document", enumerated.TypeDescription);
        Assert.Equal(enumerated.TypeDescription, resolved.TypeDescription);
    }

    [Fact]
    public void Watcher_dispose_is_idempotent()
    {
        var watcher = _provider.CreateWatcher(new VfsPath("file", _dir));
        Assert.NotNull(watcher);

        watcher!.Dispose();
        var ex = Record.Exception(() => watcher.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Disposed_watcher_stops_emitting_changes()
    {
        var watcher = _provider.CreateWatcher(new VfsPath("file", _dir));
        Assert.NotNull(watcher);

        var received = 0;
        using var subscription = watcher!.Changes.Subscribe(_ => Interlocked.Increment(ref received));

        // Dispose before any change, then mutate the directory. The watcher must be inert.
        watcher.Dispose();

        await File.WriteAllTextAsync(Path.Combine(_dir, "after-dispose.txt"), "x");
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref received));
    }
}
