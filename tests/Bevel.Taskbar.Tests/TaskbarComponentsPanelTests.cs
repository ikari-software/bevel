using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 5: one ordered list, spacer-as-component. Win2000 and Win11 are the same model
/// with different arrangements, so both are golden fixtures here.
/// </summary>
[Collection("TaskbarTheme")]
public class TaskbarComponentsPanelTests
{
    private static Control Child(ComponentSizing sizing, double width, double weight = 1.0)
    {
        var c = new Border { Width = sizing == ComponentSizing.Greedy ? double.NaN : width, Height = 24 };
        TaskbarComponentsPanel.SetSizing(c, sizing);
        TaskbarComponentsPanel.SetWeight(c, weight);
        return c;
    }

    private static TaskbarComponentsPanel Measured(double width, params Control[] children)
    {
        var p = new TaskbarComponentsPanel();
        foreach (var c in children) p.Children.Add(c);
        p.Measure(new Size(width, 30));
        p.Arrange(new Rect(0, 0, width, 30));
        return p;
    }

    [AvaloniaFact]
    public void Win2000_arrangement_gives_all_slack_to_the_greedy_strip()
    {
        var start = Child(ComponentSizing.Fixed, 74);
        var strip = Child(ComponentSizing.Greedy, 0);
        var tray = Child(ComponentSizing.Content, 100);
        Measured(500, start, strip, tray);

        Assert.Equal(0, start.Bounds.X, 1);
        Assert.Equal(74, strip.Bounds.X, 1);
        Assert.Equal(326, strip.Bounds.Width, 1);   // 500 - 74 - 100
        Assert.Equal(400, tray.Bounds.X, 1);
    }

    [AvaloniaFact]
    public void Two_equal_spacers_centre_the_group_between_them()
    {
        var left = Child(ComponentSizing.Greedy, 0);
        var start = Child(ComponentSizing.Fixed, 40);
        var strip = Child(ComponentSizing.Content, 60);
        var right = Child(ComponentSizing.Greedy, 0);
        Measured(400, left, start, strip, right);

        Assert.Equal(150, left.Bounds.Width, 1);    // (400 - 100) / 2
        Assert.Equal(150, start.Bounds.X, 1);
        Assert.Equal(150, right.Bounds.Width, 1);
        // the group is centred: equal slack either side
        Assert.Equal(left.Bounds.Width, right.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void Weight_splits_slack_proportionally()
    {
        var a = Child(ComponentSizing.Greedy, 0, weight: 3);
        var b = Child(ComponentSizing.Greedy, 0, weight: 1);
        Measured(400, a, b);

        Assert.Equal(300, a.Bounds.Width, 1);
        Assert.Equal(100, b.Bounds.Width, 1);
    }

    // Review Focus 5. Both children keep the default weight (1.0), so totalWeight == 2 here and
    // the even-split (divide-by-zero guard) branch is never reached — that branch is actually
    // exercised by Zero_weight_greedy_children_do_not_produce_NaN below. Renamed (fix round 1,
    // Minor) to describe what this test actually checks.
    [AvaloniaFact]
    public void Two_equal_weight_spacers_share_the_width_evenly()
    {
        var a = Child(ComponentSizing.Greedy, 0);
        var b = Child(ComponentSizing.Greedy, 0);
        Measured(200, a, b);

        Assert.Equal(100, a.Bounds.Width, 1);
        Assert.Equal(100, b.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void Zero_weight_greedy_children_do_not_produce_NaN()
    {
        var a = Child(ComponentSizing.Greedy, 0, weight: 0);
        var b = Child(ComponentSizing.Greedy, 0, weight: 0);
        Measured(200, a, b);

        Assert.False(double.IsNaN(a.Bounds.Width));
        Assert.Equal(100, a.Bounds.Width, 1);   // equal split when all weights are zero
    }

    [AvaloniaFact]
    public void Content_wider_than_the_bar_is_clamped_not_negative()
    {
        var wide = Child(ComponentSizing.Content, 500);
        var strip = Child(ComponentSizing.Greedy, 0);
        Measured(200, wide, strip);

        Assert.True(strip.Bounds.Width >= 0);
    }

    [AvaloniaFact]
    public void An_empty_panel_measures_without_throwing()
        => Assert.Equal(0, Measured(300).Children.Count);
}
