using Bevel.Pal.Abstractions;
using Avalonia;
using Xunit;

namespace Bevel.Taskbar.Tests;

public class TaskbarGeometryTests
{
    [Fact]
    public void BottomLeft_subtracts_the_scaled_bar_height()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        var pos = TaskbarGeometry.BottomLeft(bounds, scale: 1.5, heightDip: 30);
        // 30 DIP × 1.5 = 45 device px; Y = 1080 - 45
        Assert.Equal(new PixelPoint(0, 1035), pos);
    }

    [Fact]
    public void BottomLeft_at_scale_1_matches_the_legacy_unscaled_math()
    {
        var bounds = new PixelRect(100, 200, 800, 600);
        Assert.Equal(new PixelPoint(100, 770), TaskbarGeometry.BottomLeft(bounds, 1.0, 30));
    }

    [Fact]
    public void BottomLeft_treats_non_positive_scale_as_one()
    {
        var bounds = new PixelRect(0, 0, 1000, 500);
        Assert.Equal(TaskbarGeometry.BottomLeft(bounds, 1.0, 40), TaskbarGeometry.BottomLeft(bounds, 0, 40));
    }

    [Fact]
    public void WidthDip_divides_device_pixels_by_scale()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(1280, TaskbarGeometry.WidthDip(bounds, 1.5), 3);
        Assert.Equal(1920, TaskbarGeometry.WidthDip(bounds, 1.0), 3);
    }

    [Fact]
    public void WorkAreaBand_on_Windows_stays_in_physical_pixels()
    {
        // GetWindowRect is physical; dividing the band by scale (the old bug) parks it at mid-screen
        // on a 200% display (PR #1 #2).
        var bounds = new PixelRect(0, 0, 1920, 1080);
        var band = TaskbarGeometry.WorkAreaBand(bounds, scale: 2.0, heightDip: 30, windowManagerUsesPhysicalPixels: true);
        Assert.Equal(0, band.X);
        Assert.Equal(1020, band.Y); // 1080 - 30*2
        Assert.Equal(1920, band.Width);
        Assert.Equal(60, band.Height);
    }

    [Fact]
    public void WorkAreaBand_on_macOS_converts_physical_bounds_to_points()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        var band = TaskbarGeometry.WorkAreaBand(bounds, scale: 2.0, heightDip: 30, windowManagerUsesPhysicalPixels: false);
        Assert.Equal(0, band.X);
        Assert.Equal(510, band.Y); // 540 - 30
        Assert.Equal(960, band.Width);
        Assert.Equal(30, band.Height);
    }

    // bevel-u6ue: the area a zoom or tile fills: the whole screen width, from just under the menu bar
    // (the working area's top) to the screen bottom. The mitigator only corrects windows shaped like it.
    [Fact]
    public void ZoomArea_on_macOS_runs_from_under_the_menu_bar_to_the_screen_bottom_in_points()
    {
        var bounds = new PixelRect(0, 0, 5120, 2880);
        var working = new PixelRect(0, 60, 5120, 2820);   // a 30pt menu bar at 2x
        var area = TaskbarGeometry.ZoomArea(bounds, working, scale: 2.0, windowManagerUsesPhysicalPixels: false);
        Assert.Equal(new PalRect(0, 30, 2560, 1410), area);
    }

    [Fact]
    public void ZoomArea_on_Windows_stays_in_physical_pixels()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        var working = new PixelRect(0, 0, 1920, 1040);
        var area = TaskbarGeometry.ZoomArea(bounds, working, scale: 2.0, windowManagerUsesPhysicalPixels: true);
        Assert.Equal(new PalRect(0, 0, 1920, 1080), area);
    }
}
