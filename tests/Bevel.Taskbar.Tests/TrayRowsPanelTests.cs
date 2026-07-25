using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Row-major, width-balanced assignment for the tray grid: a wide icon takes proportional room (so it's
/// alone or with fewer neighbours), narrow icons pack together, order preserved, no row starved.
/// </summary>
public class TrayRowsPanelTests
{
    [Fact]
    public void Wide_icon_balances_against_narrow_ones()
    {
        // total 60, target 30/row → three 10s in row 0, the lone 30 in row 1.
        var rowOf = TrayRowsPanel.AssignRows(new double[] { 10, 10, 10, 30 }, 2);
        Assert.Equal(new[] { 0, 0, 0, 1 }, rowOf);
    }

    [Fact]
    public void Uniform_icons_split_evenly()
    {
        var rowOf = TrayRowsPanel.AssignRows(new double[] { 10, 10, 10, 10, 10, 10 }, 2);
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1 }, rowOf);
    }

    [Fact]
    public void Fewer_items_than_rows_all_go_to_the_first_row()
    {
        Assert.Equal(new[] { 0 }, TrayRowsPanel.AssignRows(new double[] { 20 }, 3));
    }

    [Fact]
    public void Single_row_keeps_everything_on_one_line()
    {
        Assert.Equal(new[] { 0, 0, 0 }, TrayRowsPanel.AssignRows(new double[] { 30, 10, 50 }, 1));
    }

    [Fact]
    public void No_row_is_starved_even_with_a_dominant_wide_item()
    {
        // The first item is huge; it must not swallow both rows and leave row 1 empty.
        var rowOf = TrayRowsPanel.AssignRows(new double[] { 100, 10, 10 }, 2);
        Assert.Contains(1, rowOf);            // row 1 gets at least one item
        Assert.Equal(0, rowOf[0]);            // order preserved
    }
}
