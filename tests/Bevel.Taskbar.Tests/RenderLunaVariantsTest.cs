using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
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

    /// <summary>Renders a taskbar strip with the three task-button states (default / hover / active) on
    /// the bar for each colour, so the button highlight/contrast can be judged per variant.</summary>
    [AvaloniaFact]
    public void Render_task_button_states()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_LUNA_VARIANTS_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            foreach (var (color, _) in Bevel.UI.Luna.LunaVariantService.Colors)
            {
                Bevel.UI.Luna.LunaVariantService.Apply(color, "Hybrid");
                RenderTaskbarStrip($"{color}")?.Save(Path.Combine(dir, $"taskbar-{color}.png"));
            }
        }
        finally { Bevel.UI.ThemeService.Apply("win2000"); }
    }

    private static IBrush R(string key)
    {
        Application.Current!.TryGetResource(key, null, out var v);
        return v as IBrush ?? Brushes.Magenta;
    }

    private static Avalonia.Media.Imaging.WriteableBitmap? RenderTaskbarStrip(string title)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
        (string Key, string Border, string Label)[] btns =
        {
            ("Luna.Brush.TaskButton", "Luna.Brush.TaskButtonBorder", "Documents"),
            ("Luna.Brush.TaskButtonHover", "Luna.Brush.TaskButtonBorder", "Hover"),
            ("Luna.Brush.TaskButtonChecked", "Luna.Brush.TaskButtonCheckedBorder", "Active window"),
        };
        foreach (var (k, bd, lbl) in btns)
            bar.Children.Add(new Border
            {
                Height = 22, MinWidth = 150, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                Background = R(k), BorderBrush = R(bd),
                Child = new TextBlock { Text = lbl, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = R("Bevel.Brush.TrayText") },
            });
        var strip = new Border { Background = R("Bevel.Brush.TaskbarBackground"), Height = 30, Child = bar };
        var win = new Bevel.UI.BevelWindow { Title = title, Width = 580, Height = 70, Content = strip };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return win.CaptureRenderedFrame();
    }

    /// <summary>Renders the Properties dialog's Appearance tab under Luna and Win2000 so the dynamic
    /// theme-options subpanel (Colour + Gloss for Luna; Colour scheme for Win2000) can be eyeballed.</summary>
    [AvaloniaFact]
    public async Task Render_theme_options_panel()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_LUNA_VARIANTS_DIR");
        if (string.IsNullOrEmpty(dir)) return; // opt-in
        Directory.CreateDirectory(dir);

        var cases = new[]
        {
            ("luna", "Purple", "Gloss"),
            ("win2000", "", ""),
        };
        try
        {
            foreach (var (theme, color, gloss) in cases)
            {
                Bevel.UI.ThemeService.Apply(theme);
                var sdir = Path.Combine(Path.GetTempPath(), $"bevel-onb-{theme}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(sdir);
                var settings = new SettingsService(sdir);
                await settings.UpdateAsync(s => { s.ThemeId = theme; s.LunaColor = color; s.LunaGloss = gloss; });
                Bevel.UI.ThemeVariants.Apply(settings.Current);

                var win = new OnboardingWindow(settings) { Width = 470, Height = 500 };
                win.Show();
                Dispatcher.UIThread.RunJobs();
                var tabs = win.GetVisualDescendants().OfType<TabControl>().First();
                foreach (var item in tabs.Items.OfType<TabItem>())
                    if (item.Header as string == "Appearance") { tabs.SelectedItem = item; break; }
                Dispatcher.UIThread.RunJobs();

                win.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"dialog-{theme}.png"));
            }
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
