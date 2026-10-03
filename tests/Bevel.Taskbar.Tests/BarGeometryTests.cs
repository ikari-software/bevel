using Bevel.Core;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 8: bar height is COMPOSED from component contributions, not read from a global
/// static. Two bars must be able to differ, which bevel-tjr2 (multi-monitor) requires.
/// </summary>
public class BarGeometryTests
{
    [Fact]
    public void Classic_tier_reproduces_the_Win2000_metrics_exactly()
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(BarGeometry.ButtonHeightFor(TaskbarButtonSize.Normal));
        Assert.Equal(24, g.ButtonHeight);
        Assert.Equal(28, g.RowHeight);
        Assert.Equal(30, g.TaskbarHeight);
        Assert.Equal(30, g.Height);
    }

    [Theory]
    [InlineData(TaskbarButtonSize.Small, 18, 22, 24)]
    [InlineData(TaskbarButtonSize.Normal, 24, 28, 30)]
    [InlineData(TaskbarButtonSize.Large, 30, 34, 36)]
    [InlineData(TaskbarButtonSize.Big, 40, 44, 46)]
    public void Every_tier_keeps_its_documented_metrics(TaskbarButtonSize size, int btn, int row, int bar)
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(BarGeometry.ButtonHeightFor(size));
        Assert.Equal(btn, g.ButtonHeight);
        Assert.Equal(row, g.RowHeight);
        Assert.Equal(bar, g.TaskbarHeight);
    }

    [Fact]
    public void The_tallest_contribution_wins()
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(18);
        g.Contribute(40);
        g.Contribute(24);
        Assert.Equal(40, g.ButtonHeight);
    }

    [Fact]
    public void Extra_rows_add_one_row_height_each()
    {
        var g = new BarGeometry(rows: 3);
        g.Contribute(24);
        Assert.Equal(30 + 28 + 28, g.Height);
    }

    [Fact]
    public void A_bar_with_no_components_still_has_a_usable_height()
    {
        var g = new BarGeometry(rows: 1);
        Assert.Equal(30, g.Height);   // falls back to the Normal tier rather than collapsing to zero
    }

    [Fact]
    public void Two_bars_hold_independent_geometry()
    {
        var a = new BarGeometry(rows: 1); a.Contribute(40);
        var b = new BarGeometry(rows: 1); b.Contribute(18);
        Assert.Equal(40, a.ButtonHeight);
        Assert.Equal(18, b.ButtonHeight);
    }

    [Fact]
    public void Rows_below_one_are_clamped()
        => Assert.Equal(30, new BarGeometry(rows: 0).Height);

    // Fix round 1, Finding 1: Contribute (anonymous) and SetContribution/RemoveContribution (keyed)
    // share ONE backing store. Mixing them on one instance must not silently discard either side.
    [Fact]
    public void Bare_and_keyed_contributions_share_one_backing_store()
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(40);
        g.SetContribution("a", 18);
        Assert.Equal(40, g.ButtonHeight);   // the max across both the anonymous and keyed forms

        g.RemoveContribution("a");
        Assert.Equal(40, g.ButtonHeight);   // the anonymous contribution survives the keyed removal
    }
}
