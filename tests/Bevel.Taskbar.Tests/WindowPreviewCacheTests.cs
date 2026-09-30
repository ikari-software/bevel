using Xunit;

namespace Bevel.Taskbar.Tests;

public class WindowPreviewCacheTests
{
    [Fact]
    public void TryGet_miss_returns_false()
    {
        var c = new WindowPreviewCache();
        Assert.False(c.TryGet("missing", out var png));
        Assert.Null(png);
    }

    [Fact]
    public void Set_then_TryGet_returns_same_bytes()
    {
        var c = new WindowPreviewCache();
        var bytes = new byte[] { 1, 2, 3, 4 };
        c.Set("w1", bytes);
        Assert.True(c.TryGet("w1", out var got));
        Assert.Equal(bytes, got);
    }

    [Fact]
    public void Capacity_evicts_least_recently_used()
    {
        var c = new WindowPreviewCache(capacity: 2);
        c.Set("a", new byte[] { 1 });
        c.Set("b", new byte[] { 2 });
        Assert.True(c.TryGet("a", out _)); // touch a → b is now LRU
        c.Set("c", new byte[] { 3 });
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Empty_png_is_ignored()
    {
        var c = new WindowPreviewCache();
        c.Set("w", Array.Empty<byte>());
        Assert.Equal(0, c.Count);
        Assert.False(c.TryGet("w", out _));
    }
}
