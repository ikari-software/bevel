using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 15: a surface degrades ONLY inside its own region, and only if it still carries
/// its accessible identity and repaints on a theme switch. Without the theme push a surface goes
/// stale on every switch — which is bevel-voqo exactly.
/// </summary>
[Collection("TaskbarTheme")]
public class SurfaceHostTests
{
    private static SurfacePrimitive Face() =>
        new("face", 32, 16, AccessibleName: "Clock face", AccessibleRole: "Image");

    [AvaloniaFact]
    public void A_surface_view_carries_its_declared_accessible_name()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        var view = host.CreateView(Face(), "inst-a");
        Assert.Equal("Clock face", AutomationProperties.GetName(view));
    }

    [AvaloniaFact]
    public void A_surface_view_honours_its_declared_intrinsic_size()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        var view = host.CreateView(Face(), "inst-a");
        Assert.Equal(32, view.Width);
        Assert.Equal(16, view.Height);
    }

    [AvaloniaFact]
    public void Only_the_owning_instance_may_publish_a_frame()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        host.CreateView(Face(), "inst-a");
        host.CreateView(Face(), "inst-b");

        Assert.True(host.TryAcceptFrame("inst-a", host.SlotOf("inst-a", "face"), 1));
        Assert.False(host.TryAcceptFrame("inst-b", host.SlotOf("inst-a", "face"), 1));
    }

    [AvaloniaFact]
    public void A_stale_frame_number_is_refused()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        host.CreateView(Face(), "inst-a");
        var slot = host.SlotOf("inst-a", "face");

        Assert.True(host.TryAcceptFrame("inst-a", slot, 5));
        Assert.False(host.TryAcceptFrame("inst-a", slot, 4));   // out-of-order delivery
    }

    // Spec §6: a component dying mid-frame must not leave its last image on screen forever.
    [AvaloniaFact]
    public void Marking_inert_refuses_further_frames_from_that_instance()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        host.CreateView(Face(), "inst-a");
        var slot = host.SlotOf("inst-a", "face");

        host.MarkInert("inst-a");
        Assert.False(host.TryAcceptFrame("inst-a", slot, 1));
    }

    // The bevel-voqo regression guard: a theme switch must bump the revision components observe,
    // or every surface keeps painting the old palette.
    [AvaloniaFact]
    public void Pushing_a_theme_bumps_the_revision_components_observe()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        var before = host.Revision;
        host.PushTheme(new Dictionary<string, string> { ["Bevel.Brush.TaskbarBackground"] = "#FFD4D0C8" });
        Assert.True(host.Revision > before);
    }

    [AvaloniaFact]
    public void Pushing_a_theme_twice_bumps_twice_so_a_colourway_re_hue_is_not_coalesced_away()
    {
        var host = new SurfaceHost(new SurfaceOwnership());
        host.PushTheme(new Dictionary<string, string> { ["x"] = "#FF000000" });
        var mid = host.Revision;
        host.PushTheme(new Dictionary<string, string> { ["x"] = "#FFFFFFFF" });
        Assert.True(host.Revision > mid);
    }
}
