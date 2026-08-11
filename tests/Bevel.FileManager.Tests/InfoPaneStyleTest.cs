using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>Every InfoPane style (Folder Options) instantiates and renders without throwing when driven
/// through the shared data API. Guards against a broken style XAML/binding. Set BEVEL_INFO_DIR to dump the
/// PNGs for eyeballing.</summary>
public class InfoPaneStyleTest
{
    [AvaloniaTheory]
    [InlineData(InfoPaneStyle.Win2000, "win2000")]
    [InlineData(InfoPaneStyle.WinXP, "winxp")]
    [InlineData(InfoPaneStyle.Modern, "modern")]
    [InlineData(InfoPaneStyle.Win9x, "win9x")]
    public void Each_style_renders(InfoPaneStyle style, string tag)
    {
        var pane = new InfoPane { Style = style, Width = 200, Height = 380 };
        pane.Title = "Documents";
        pane.Description = "Displays the files and folders in this location.";
        pane.ObjectCount = "14 object(s)";
        pane.ClearTasks();
        pane.AddTask("Make a new folder", () => { });
        pane.AddTask("View folder properties", () => { });
        pane.ClearLinks();
        pane.AddLink("My Documents", () => { });
        pane.AddLink("My Computer", () => { });

        var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width >= 190, $"{tag} too narrow: {frame.PixelSize}");

        if (Environment.GetEnvironmentVariable("BEVEL_INFO_DIR") is { } dir)
            frame.Save(Path.Combine(dir, $"infopane-{tag}.png"));
    }

    [AvaloniaFact]
    public void Modern_rows_are_backed_by_real_navigation_and_command_actions()
    {
        var invoked = new System.Collections.Generic.List<string>();
        var pane = new InfoPane { Style = InfoPaneStyle.Modern, Width = 200, Height = 380 };
        pane.AddLink("My Documents", () => invoked.Add("place"));
        pane.AddTask("Make a new folder", () => invoked.Add("command"));

        var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var actions = pane.GetVisualDescendants()
            .OfType<Border>()
            .Select(border => border.Tag)
            .OfType<Action>()
            .ToArray();

        Assert.Equal(2, actions.Length);
        foreach (var action in actions) action();
        Assert.Equal(new[] { "place", "command" }, invoked);
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task WinXP_chevrons_are_centered_functional_and_animated()
    {
        var pane = new InfoPane { Style = InfoPaneStyle.WinXP, Width = 200, Height = 380 };
        pane.AddTask("Make a new folder", () => { });
        pane.AddLink("My Documents", () => { });
        var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var toggles = pane.GetVisualDescendants().OfType<ToggleButton>()
            .Where(toggle => toggle.Classes.Contains("XpChevron"))
            .ToArray();
        Assert.Equal(3, toggles.Length);

        foreach (var toggle in toggles)
        {
            Assert.Equal(16, toggle.Bounds.Width);
            Assert.Equal(16, toggle.Bounds.Height);
            var chevron = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            var bounds = chevron.Data!.Bounds;
            Assert.Equal(8, (bounds.Left + bounds.Right) / 2, 3);
            Assert.Equal(8, (bounds.Top + bounds.Bottom) / 2, 3);
        }

        var tasksToggle = toggles.Single(toggle => toggle.Name == "XpTasksToggle");
        var tasksBody = pane.FindControl<StackPanel>("XpTasksPanel")!;
        var tasksChevron = tasksToggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        var rotation = Assert.IsType<RotateTransform>(tasksChevron.RenderTransform);

        tasksToggle.IsChecked = false;
        tasksToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await System.Threading.Tasks.Task.Delay(190);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tasksBody.IsVisible);
        Assert.Equal(180, rotation.Angle, 3);

        tasksToggle.IsChecked = true;
        tasksToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await System.Threading.Tasks.Task.Delay(190);
        Dispatcher.UIThread.RunJobs();
        Assert.True(tasksBody.IsVisible);
        Assert.Equal(1, tasksBody.Opacity, 3);
        Assert.Equal(0, rotation.Angle, 3);
    }

    /// <summary>The WinXP (Luna) info-pane re-hues to the active Luna colour variant (bevel-e544): its
    /// watermark + group-box headers/borders should shift HUE to the variant while staying light. Set
    /// BEVEL_INFO_DIR to dump one PNG per variant for eyeballing.</summary>
    [AvaloniaTheory]
    [InlineData("Blue")]
    [InlineData("Silver")]
    [InlineData("Black")]
    [InlineData("Purple")]
    public void WinXP_recolours_to_luna_variant(string color)
    {
        Bevel.UI.Luna.LunaVariantService.Apply(color, "Hybrid");
        try
        {
            AssertContrast("Luna.Brush.InfoPaneHeadingText", 4.5);
            AssertContrast("Luna.Brush.InfoPaneBodyText", 4.5);
            AssertContrast("Luna.Brush.InfoPaneLinkText", 4.5);
            AssertContrast("Luna.Brush.InfoPaneMutedText", 3.0);

            var pane = new InfoPane { Style = InfoPaneStyle.WinXP, Width = 200, Height = 380 };
            pane.Title = "ikari";
            pane.Description = "Displays the files and folders in this location.";
            pane.ObjectCount = "265 object(s)";
            pane.ClearTasks();
            pane.AddTask("Make a new folder", () => { });
            pane.AddTask("View folder properties", () => { });
            pane.ClearLinks();
            pane.AddLink("My Documents", () => { });
            pane.AddLink("My Computer", () => { });

            var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("BEVEL_INFO_DIR") is { } dir)
                frame!.Save(Path.Combine(dir, $"infopane-luna-{color}.png"));
        }
        finally
        {
            Bevel.UI.Luna.LunaVariantService.Clear();
        }
    }

    private static void AssertContrast(string resourceKey, double minimum)
    {
        Assert.True(Application.Current!.TryFindResource(resourceKey, null, out var value));
        var brush = Assert.IsType<SolidColorBrush>(value);
        var card = Color.Parse("#F8FAFF");
        var ratio = ContrastRatio(brush.Color, card);
        Assert.True(ratio >= minimum, $"{resourceKey} contrast {ratio:F2}:1 is below {minimum:F1}:1");
    }

    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte value)
            {
                var s = value / 255.0;
                return s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var l1 = Luminance(a);
        var l2 = Luminance(b);
        return (System.Math.Max(l1, l2) + 0.05) / (System.Math.Min(l1, l2) + 0.05);
    }

    [AvaloniaFact]
    public void Open_WinXP_pane_tracks_live_variant_changes_and_restores_its_fallback()
    {
        Bevel.UI.Luna.LunaVariantService.Clear();
        var pane = new InfoPane { Style = InfoPaneStyle.WinXP, Width = 200, Height = 380 };
        var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var fallback = pane.Resources["InfoPane.Xp.Watermark"];
            Bevel.UI.Luna.LunaVariantService.Apply("Purple", "Hybrid");
            var purple = pane.Resources["InfoPane.Xp.Watermark"];
            Assert.NotSame(fallback, purple);

            Bevel.UI.Luna.LunaVariantService.Clear();
            Assert.Same(fallback, pane.Resources["InfoPane.Xp.Watermark"]);
        }
        finally
        {
            Bevel.UI.Luna.LunaVariantService.Clear();
            win.Close();
        }
    }
}
