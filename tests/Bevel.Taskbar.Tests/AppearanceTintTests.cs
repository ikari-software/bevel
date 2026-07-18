using Avalonia.Media;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unit coverage for the taskbar background tint policy (bevel-cust.appearance). Exercises the pure
/// <see cref="TaskbarView.ComputeTintColor"/> seam: null return means "fall back to the theme brush"
/// (so the caller clears the local value and keeps following theme switches), a custom hex wins, an
/// opacity-only setting tints the theme colour, and unparseable/empty hex is ignored (never throws).
/// </summary>
public class AppearanceTintTests
{
    private static readonly Color Theme = Color.FromRgb(200, 200, 200);

    [Fact]
    public void Opaque_and_no_color_returns_null_so_the_theme_brush_applies()
        => Assert.Null(TaskbarView.ComputeTintColor("", 100, Theme));

    [Fact]
    public void Custom_hex_wins_and_takes_the_opacity_alpha()
    {
        var c = TaskbarView.ComputeTintColor("#204060", 50, Theme);
        Assert.NotNull(c);
        Assert.Equal(0x20, c!.Value.R);
        Assert.Equal(0x40, c.Value.G);
        Assert.Equal(0x60, c.Value.B);
        Assert.Equal((byte)(50 * 255 / 100), c.Value.A);   // 127
    }

    [Fact]
    public void Opacity_only_tints_the_theme_color()
    {
        var c = TaskbarView.ComputeTintColor("", 60, Theme);
        Assert.NotNull(c);
        Assert.Equal(Theme.R, c!.Value.R);
        Assert.Equal((byte)(60 * 255 / 100), c.Value.A);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("#zz")]
    public void Invalid_or_empty_hex_never_throws_and_falls_back_to_the_theme(string bad)
    {
        // Opaque + no usable colour → null (theme brush re-applies).
        Assert.Null(TaskbarView.ComputeTintColor(bad, 100, Theme));
        // With opacity, an unusable colour tints the theme colour instead — never throws.
        var c = TaskbarView.ComputeTintColor(bad, 50, Theme);
        Assert.Equal(Theme.R, c!.Value.R);
    }

    [Fact]
    public void Named_colors_are_accepted_as_a_valid_custom_tint()
        => Assert.Equal(Colors.Red, TaskbarView.ComputeTintColor("red", 100, Theme)!.Value);

    [Fact]
    public void Opacity_below_the_floor_is_clamped_not_zero()
    {
        var c = TaskbarView.ComputeTintColor("#000000", 0, Theme);   // 0 clamps to 20
        Assert.Equal((byte)(20 * 255 / 100), c!.Value.A);
    }

    [Fact]
    public void Null_theme_color_with_opacity_falls_back_without_throwing()
        => Assert.NotNull(TaskbarView.ComputeTintColor("", 50, null));   // Silver-based, no throw
}
