using Bevel.App.Updates;
using Bevel.Core.Updates;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-ym0: the Velopack-backed IUpdateSource. Two guarantees matter most: it never throws on a
/// broken feed (an unreachable feed must look like "up to date"), and an empty feed URL short-circuits
/// without any network attempt.
/// </summary>
public class VelopackUpdateSourceTests
{
    [Fact]
    public async Task Empty_feed_url_returns_None_without_network()
    {
        var src = new VelopackUpdateSource();
        var result = await src.CheckAsync("", "0.1.0", CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal(UpdateAvailability.None, result);
    }

    [Fact]
    public async Task Unreachable_feed_returns_None_never_throws()
    {
        var src = new VelopackUpdateSource();
        // A URL that cannot resolve / will 404 — must not throw.
        var result = await src.CheckAsync(
            "https://bevel.run/updates-nonexistent-feed-xyz/", "0.1.0", CancellationToken.None);
        Assert.False(result.Available);
    }

    [Fact]
    public async Task Malformed_feed_url_returns_None_never_throws()
    {
        var src = new VelopackUpdateSource();
        var result = await src.CheckAsync("not-a-url", "0.1.0", CancellationToken.None);
        Assert.False(result.Available);
    }

    [Fact]
    public async Task Local_dir_with_no_valid_feed_returns_None_never_throws()
    {
        // A local directory that exists but has no valid Velopack feed must not throw.
        var dir = Path.Combine(Path.GetTempPath(), "bevel-vpfeed-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "garbage.txt"), "not a feed");
            var src = new VelopackUpdateSource();
            var result = await src.CheckAsync(dir, "0.1.0", CancellationToken.None);
            Assert.False(result.Available);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Local_empty_dir_returns_None()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-vpfeed-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = new VelopackUpdateSource();
            var result = await src.CheckAsync(dir, "0.1.0", CancellationToken.None);
            Assert.False(result.Available);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
