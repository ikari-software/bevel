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
/// Dev-only visual render of the Luna variant matrix (colour × gloss) via Blue2001VariantService, so all
/// 12 looks can be eyeballed. Set BEVEL_LUNA_VARIANTS_DIR to dump one PNG per combo (colour-gloss.png).
/// Blue-Hybrid must match the current tuned look (the colour axis is identity for Blue).
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderBlue2001VariantsTest
{
    [AvaloniaFact]
    public void Render_luna_variant_matrix()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_LUNA_VARIANTS_DIR");
        if (string.IsNullOrEmpty(dir)) return; // opt-in; no-op in CI
        Directory.CreateDirectory(dir);

        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            foreach (var (color, _) in Bevel.UI.Blue2001.Blue2001VariantService.Colors)
            foreach (var (gloss, _) in Bevel.UI.Blue2001.Blue2001VariantService.Glosses)
            {
                Bevel.UI.Blue2001.Blue2001VariantService.Apply(color, gloss);
                var frame = RenderGallery($"{color} · {gloss}");
                frame?.Save(Path.Combine(dir, $"{color}-{gloss}.png"));
            }
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    private static Bevel.UI.BevelWindow BuildGallery(string title)
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
        combo.Items.Add("Bevel");
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
        return window;
    }

    private static Avalonia.Media.Imaging.WriteableBitmap? RenderGallery(string title)
        => BuildGallery(title).CaptureRenderedFrame();

    /// <summary>
    /// site/shots/luna-4up.png — the four colourways the landing page shows side by side.
    ///
    /// This was a hand-composited file with no test behind it, which is how the other unproduced shot
    /// (theme-win2000.png) went stale for months. Layout matches what it replaces: 372x340 tiles in a
    /// 2x2 with an 8px gutter, but the tile size is MEASURED rather than assumed.
    /// </summary>
    [AvaloniaFact]
    public void Render_site_four_up()
    {
        var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_4UP_OUT");
        if (string.IsNullOrEmpty(outPath)) return;   // opt-in, like the rest of this file

        const int Gutter = 8;
        var scale = Bevel.TestSupport.SiteShot.Scale;
        int tileW = 0, tileH = 0;

        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);

            // Each tile is rendered at SiteShot.Scale, written out, and reloaded as a plain 96-dpi
            // bitmap. That round trip is the point: a RenderTargetBitmap carries its 192 dpi with it, and
            // every attempt to compose those directly — DrawingContext rects, an Image with an explicit
            // Width, an explicit Stretch — had the tile painted at one source pixel per LOGICAL unit and
            // clipped, magnifying each cell 2x. Reloaded at 96 dpi, one unit is one device pixel through
            // the whole composite and there is nothing left to get wrong.
            var staging = Directory.CreateTempSubdirectory("bevel-4up-");
            try
            {
                var tiles = new System.Collections.Generic.List<Avalonia.Media.Imaging.Bitmap>();
                var colors = new[] { "Blue", "Silver", "Black", "Purple" };
                foreach (var color in colors)
                {
                    Bevel.UI.Blue2001.Blue2001VariantService.Apply(color, Bevel.UI.Blue2001.Blue2001VariantService.DefaultGloss);
                    var window = BuildGallery($"{color} · {Bevel.UI.Blue2001.Blue2001VariantService.DefaultGloss}");

                    // Measure rather than assume. The file this replaces hard-coded 372x340 tiles.
                    var frame = window.CaptureRenderedFrame()
                                ?? throw new InvalidOperationException($"{color} gallery rendered nothing");
                    if (tileW == 0) (tileW, tileH) = (frame.PixelSize.Width, frame.PixelSize.Height);
                    Assert.Equal(new PixelSize(tileW, tileH), frame.PixelSize);   // a ragged grid is a bug

                    var path = Path.Combine(staging.FullName, $"{color}.png");
                    Bevel.TestSupport.SiteShot.Save(window, path);
                    tiles.Add(new Avalonia.Media.Imaging.Bitmap(path));
                }

                var cellW = (int)(tileW * scale);
                var cellH = (int)(tileH * scale);
                var gap = (int)(Gutter * scale);
                var canvas = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new PixelSize(cellW * 2 + gap * 3, cellH * 2 + gap * 3), new Vector(96, 96));
                using (var ctx = canvas.CreateDrawingContext())
                {
                    // The gutter colour the replaced composite used, so the swap is invisible on the page.
                    ctx.FillRectangle(new SolidColorBrush(Color.Parse("#8F8B81")),
                        new Rect(0, 0, cellW * 2 + gap * 3, cellH * 2 + gap * 3));
                    for (var i = 0; i < tiles.Count; i++)
                        ctx.DrawImage(tiles[i], new Rect(
                            gap + (i % 2) * (cellW + gap), gap + (i / 2) * (cellH + gap), cellW, cellH));
                }
                canvas.Save(outPath);
                canvas.Dispose();
                foreach (var t in tiles) t.Dispose();
            }
            finally { try { staging.Delete(true); } catch { /* best-effort cleanup */ } }
        }
        finally
        {
            Bevel.UI.Blue2001.Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
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
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            foreach (var (color, _) in Bevel.UI.Blue2001.Blue2001VariantService.Colors)
            {
                Bevel.UI.Blue2001.Blue2001VariantService.Apply(color, "Hybrid");
                RenderTaskbarStrip($"{color}")?.Save(Path.Combine(dir, $"taskbar-{color}.png"));
            }
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
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
            ("Blue2001.Brush.TaskButton", "Blue2001.Brush.TaskButtonBorder", "Documents"),
            ("Blue2001.Brush.TaskButtonHover", "Blue2001.Brush.TaskButtonBorder", "Hover"),
            ("Blue2001.Brush.TaskButtonChecked", "Blue2001.Brush.TaskButtonCheckedBorder", "Active window"),
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
            (ThemeIds.Blue2001, "Purple", "Gloss"),
            (ThemeIds.Industrial1999, "", ""),
        };
        try
        {
            foreach (var (theme, color, gloss) in cases)
            {
                Bevel.UI.ThemeService.Apply(theme);
                var sdir = Path.Combine(Path.GetTempPath(), $"bevel-onb-{theme}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(sdir);
                var settings = new SettingsService(sdir);
                await settings.UpdateAsync(s => { s.ThemeId = theme; s.Blue2001Color = color; s.Blue2001Gloss = gloss; });
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
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}
