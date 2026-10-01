using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.UI.Luna;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// The gloss axis must not flip a surface's light direction (bevel-eu0c). A raised face is lit from
/// above (light top, dim body); a pressed/active face is the inverse (shadowed top inner edge,
/// lifting toward the bottom). Flattening to Matte used to run every Control surface through the one
/// raised ramp, so the active task button came out looking raised — "the gradient goes the other way".
///
/// Hybrid and Gloss never showed it because a Control surface's stored stops are already authored for
/// them, so those paths keep the authored arrangement and never reach the flatten.
/// </summary>
public class LunaGlossRampTests
{
    private static (double First, double Last) Ends(string key)
    {
        var brush = Assert.IsType<LinearGradientBrush>(Application.Current!.Resources[key]);
        Assert.True(brush.GradientStops.Count >= 2, $"{key} is not a ramp");
        var ordered = brush.GradientStops.OrderBy(s => s.Offset).ToList();
        return (Lightness(ordered[0].Color), Lightness(ordered[^1].Color));
    }

    /// <summary>Rec. 601 luma is plenty to compare the two ends of one ramp.</summary>
    private static double Lightness(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    [AvaloniaTheory]
    [InlineData("Matte")]
    [InlineData("Hybrid")]
    [InlineData("Gloss")]
    public void Active_task_button_reads_sunken_and_resting_reads_raised_in_every_gloss(string gloss)
    {
        try
        {
            LunaVariantService.Apply("Blue", gloss);

            var resting = Ends("Luna.Brush.TaskButton");
            var active = Ends("Luna.Brush.TaskButtonChecked");

            Assert.True(resting.First > resting.Last,
                $"{gloss}: resting button should be lit from above (top {resting.First:F3} > bottom {resting.Last:F3})");
            Assert.True(active.First < active.Last,
                $"{gloss}: active button must read SUNKEN — top {active.First:F3} should be darker than bottom {active.Last:F3}");
        }
        finally { LunaVariantService.Clear(); }
    }

    /// <summary>The direction has to survive every colourway, not just the default blue — the matte
    /// flatten derives its base from the re-hued stops, so a colourway could reintroduce the flip.</summary>
    [AvaloniaTheory]
    [InlineData("Blue")]
    [InlineData("Silver")]
    [InlineData("Black")]
    [InlineData("Purple")]
    public void Matte_active_button_stays_sunken_in_every_colourway(string color)
    {
        try
        {
            LunaVariantService.Apply(color, "Matte");
            var active = Ends("Luna.Brush.TaskButtonChecked");
            Assert.True(active.First < active.Last,
                $"Matte/{color}: active button inverted — top {active.First:F3}, bottom {active.Last:F3}");
        }
        finally { LunaVariantService.Clear(); }
    }
}
