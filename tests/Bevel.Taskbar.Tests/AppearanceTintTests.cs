using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unit coverage for the taskbar fill's opacity policy (bevel-cust.appearance). The fill is applied as
/// RGBA: the themed brush (Luna's gradient / Win2000's grey) is kept and only its ALPHA is scaled via
/// the background layer's opacity — so the stored percentage maps to a 0–1 opacity, clamped to a 20–100
/// floor so the bar can never disappear entirely.
/// </summary>
public class AppearanceTintTests
{
    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(80, 0.8)]
    [InlineData(60, 0.6)]
    [InlineData(20, 0.2)]
    public void Percentage_maps_to_a_0_to_1_fill_opacity(int percent, double expected)
        => Assert.Equal(expected, TaskbarView.FillOpacity(percent), precision: 3);

    [Theory]
    [InlineData(0)]     // below the floor
    [InlineData(19)]
    [InlineData(-40)]
    public void Opacity_below_the_floor_clamps_to_20_percent_not_zero(int percent)
        => Assert.Equal(0.2, TaskbarView.FillOpacity(percent), precision: 3);

    [Theory]
    [InlineData(101)]
    [InlineData(500)]
    public void Opacity_above_full_clamps_to_fully_opaque(int percent)
        => Assert.Equal(1.0, TaskbarView.FillOpacity(percent), precision: 3);
}
