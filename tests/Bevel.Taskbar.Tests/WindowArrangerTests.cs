using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>Pure layout math behind the bar menu's Cascade/Tile commands (bevel-apm).</summary>
public class WindowArrangerTests
{
    private static readonly PalRect Area = new(100, 50, 1440, 900);

    [Fact]
    public void Tile_vertically_splits_the_area_into_equal_columns_with_no_gap_or_overlap()
    {
        var rects = WindowArranger.TileVertically(Area, 3);

        Assert.Equal(3, rects.Count);
        Assert.All(rects, r =>
        {
            Assert.Equal(Area.Y, r.Y);
            Assert.Equal(Area.Height, r.Height);
        });
        for (var i = 0; i < rects.Count; i++)
        {
            Assert.Equal(480, rects[i].Width);
            Assert.Equal(Area.X + i * 480, rects[i].X);
            if (i > 0)
                Assert.Equal(rects[i - 1].X + rects[i - 1].Width, rects[i].X); // no gap, no overlap
        }
        Assert.Equal(Area.X + Area.Width, rects[^1].X + rects[^1].Width); // covers the full width
    }

    [Fact]
    public void Tile_horizontally_splits_the_area_into_equal_rows_with_no_gap_or_overlap()
    {
        var rects = WindowArranger.TileHorizontally(Area, 4);

        Assert.Equal(4, rects.Count);
        Assert.All(rects, r =>
        {
            Assert.Equal(Area.X, r.X);
            Assert.Equal(Area.Width, r.Width);
        });
        for (var i = 0; i < rects.Count; i++)
        {
            Assert.Equal(225, rects[i].Height);
            Assert.Equal(Area.Y + i * 225, rects[i].Y);
            if (i > 0)
                Assert.Equal(rects[i - 1].Y + rects[i - 1].Height, rects[i].Y); // no gap, no overlap
        }
        Assert.Equal(Area.Y + Area.Height, rects[^1].Y + rects[^1].Height); // covers the full height
    }

    [Fact]
    public void Cascade_offsets_each_window_and_keeps_all_inside_the_area()
    {
        var rects = WindowArranger.Cascade(Area, 5);

        Assert.Equal(5, rects.Count);
        Assert.Equal(new PalRect(Area.X, Area.Y, 720, 600), rects[0]); // starts at the area origin
        foreach (var r in rects)
        {
            Assert.True(r.X >= Area.X && r.Y >= Area.Y, $"origin inside the area: {r}");
            Assert.True(r.X + r.Width <= Area.X + Area.Width && r.Y + r.Height <= Area.Y + Area.Height,
                $"extent inside the area: {r}");
        }
        // a real cascade, not a stack: each window is offset from the previous one
        Assert.True(rects[1].X > rects[0].X || rects[1].Y > rects[0].Y);
        Assert.NotEqual(rects[0], rects[1]);
    }

    [Fact]
    public void Zero_windows_returns_empty()
    {
        Assert.Empty(WindowArranger.Cascade(Area, 0));
        Assert.Empty(WindowArranger.TileVertically(Area, 0));
        Assert.Empty(WindowArranger.TileHorizontally(Area, 0));
    }
}
