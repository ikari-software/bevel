using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the Luna variant matrix (colour × gloss) via LunaVariantService, so all
/// 12 looks can be eyeballed. Set BEVEL_LUNA_VARIANTS_DIR to dump one PNG per combo (colour-gloss.png).
/// Blue-Hybrid must match the current tuned look (the colour axis is identity for Blue).
/// </summary>
public class RenderLunaVariantsTest
{
    [AvaloniaFact]
    public void Render_luna_variant_matrix()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_LUNA_VARIANTS_DIR");
        if (string.IsNullOrEmpty(dir)) return; // opt-in; no-op in CI
        Directory.CreateDirectory(dir);

        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            foreach (var (color, _) in Bevel.UI.Luna.LunaVariantService.Colors)
            foreach (var (gloss, _) in Bevel.UI.Luna.LunaVariantService.Glosses)
            {
                Bevel.UI.Luna.LunaVariantService.Apply(color, gloss);
                var frame = RenderGallery($"{color} · {gloss}");
                frame?.Save(Path.Combine(dir, $"{color}-{gloss}.png"));
            }
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    private static Avalonia.Media.Imaging.WriteableBitmap? RenderGallery(string title)
    {
        var body = new StackPanel { Margin = new Thickness(14), Spacing = 10, Width = 340 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(new Button { Content = "OK", MinWidth = 73, IsDefault = true });
        buttons.Children.Add(new Button { Content = "Cancel", MinWidth = 73 });
        body.Children.Add(buttons);

        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        checks.Children.Add(new CheckBox { Content = "Enabled", IsChecked = true });
        checks.Children.Add(new RadioButton { Content = "Selected", IsChecked = true });
        body.Children.Add(checks);

        body.Children.Add(new ProgressBar { Value = 62, Minimum = 0, Maximum = 100, Height = 16 });

        var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Luna");
        combo.SelectedIndex = 0;
        body.Children.Add(combo);

        var group = new HeaderedContentControl
        {
            Header = "Appearance",
            Content = new TextBlock { Text = "Group box content", Margin = new Thickness(8) },
        };
        body.Children.Add(group);

        var window = new Bevel.UI.BevelWindow
        {
            Title = title,
            Width = 372,
            Height = 340,
            Background = new SolidColorBrush(Color.Parse("#ECE9D8")),
            Content = body,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window.CaptureRenderedFrame();
    }
}
