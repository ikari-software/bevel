using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the Start-button badges (Bevel mark at 16/20/32/96 + Tux) on the Classic
/// and Luna Start faces to a PNG, so the vector geometry can be eyeballed on any host OS — the live app
/// only ever shows one badge. Plus guards that no vendor logo badge can come back.
/// </summary>
public class RenderStartLogoTest
{
    [AvaloniaFact]
    public void Render_badges_to_png_on_classic_and_luna_faces()
    {
        // Real sizes on both Start faces, so the mark is judged where it actually lives: the Start badge
        // (20, glass), the same badge with the "Detailed" option on, the About-box size (96, full), and Tux.
        static Control Row(IBrush face) => new Border
        {
            Background = face,
            Padding = new Avalonia.Thickness(16),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    StartLogo.Build(StartLogo.Kind.Bevel, 16),
                    StartLogo.Build(StartLogo.Kind.Bevel, 20),
                    StartLogo.Build(StartLogo.Kind.Bevel, 20, fullDetail: true),
                    StartLogo.Build(StartLogo.Kind.Bevel, 32),
                    StartLogo.Build(StartLogo.Kind.Bevel, 96),
                    StartLogo.Build(StartLogo.Kind.Tux, 96),
                },
            },
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new StackPanel
            {
                Children =
                {
                    Row(new SolidColorBrush(Color.Parse("#D4D0C8"))),   // Classic button face
                    Row(new SolidColorBrush(Color.Parse("#3C9A2E"))),   // Luna Start pill
                },
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 300, $"row too narrow: {frame.PixelSize}");

        var outPath = Environment.GetEnvironmentVariable("BEVEL_RENDER_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-startlogo-render.png");
        frame.Save(outPath);
    }

    [Fact]
    public void Only_the_bevel_mark_and_tux_exist_as_badges()
    {
        // Guard against reintroducing a vendor logo: embedding the Windows flag or the Apple mark in the
        // Start button is the highest-risk IP item (bevel-legal-branding). There is no Kind for either.
        Assert.Equal(new[] { "Bevel", "Tux" }, Enum.GetNames<StartLogo.Kind>().OrderBy(n => n));
    }

    [AvaloniaFact]
    public void Vendor_badge_assets_are_not_shipped()
    {
        foreach (var name in new[] { "windows.svg", "apple.svg", "apple.png", "windows.png" })
            Assert.False(Avalonia.Platform.AssetLoader.Exists(
                new Uri($"avares://Bevel.Taskbar/Assets/StartBadge/{name}")), $"{name} must not ship");
        Assert.True(Avalonia.Platform.AssetLoader.Exists(
            new Uri("avares://Bevel.Taskbar/Assets/StartBadge/tux.svg")), "Linux keeps Tux");
    }

    [AvaloniaFact]
    public void Linux_keeps_tux_everywhere_else_gets_the_bevel_mark()
    {
        var badge = StartLogo.For(20);
        var canvas = Assert.IsType<Canvas>(Assert.IsType<Viewbox>(badge).Child);
        // The Bevel mark is a fixed 100-unit canvas; Tux's is its own SVG viewBox.
        if (OperatingSystem.IsLinux()) Assert.NotEqual(100, canvas.Width);
        else Assert.Equal(100, canvas.Width);
    }
}
