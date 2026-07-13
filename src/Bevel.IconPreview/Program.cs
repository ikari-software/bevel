using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.UI;

namespace Bevel.IconPreview;

internal static class Program
{
    // File-type glyphs. Shared by ExportAllPngs() and PreviewWindow.BuildPreview()
    // so a glyph add/rename lives in one place. Dictionary preserves insertion
    // order, which both call sites rely on for display/export ordering.
    public static readonly Dictionary<string, IconKey> GlyphMap = new()
    {
        ["folder"] = IconKey.Folder(),
        ["folder-open"] = IconKey.Folder(16),
        ["drive-fixed"] = IconKey.FixedDrive(),
        ["drive-cd"] = IconKey.CdDrive(),
        ["drive-net"] = IconKey.NetDrive(),
        ["drive-removable"] = IconKey.RemovableDrive(),
        ["computer"] = IconKey.Computer(),
        ["network"] = new IconKey("network"),
        ["trash-empty"] = IconKey.Trash(false),
        ["trash-full"] = IconKey.Trash(true),
        ["doc-generic"] = IconKey.File("txt"),
        ["doc-exe"] = IconKey.File("exe"),
        ["doc-png"] = IconKey.File("png"),
        ["doc-zip"] = IconKey.File("zip"),
        ["doc-mp3"] = IconKey.File("mp3"),
        ["doc-avi"] = IconKey.File("avi"),
        ["doc-htm"] = IconKey.File("htm"),
        ["doc-dll"] = IconKey.File("dll"),
        ["doc-bat"] = IconKey.File("bat"),
        ["doc-ttf"] = IconKey.File("ttf"),
        ["doc-pdf"] = IconKey.File("pdf"),
        ["doc-xls"] = IconKey.File("xls"),
        ["doc-doc"] = IconKey.File("doc"),
        ["doc-ppt"] = IconKey.File("ppt"),
        ["doc-db"] = IconKey.File("db"),
        ["doc-xml"] = IconKey.File("xml"),
        ["doc-iso"] = IconKey.File("iso"),
    };

    // Toolbar glyphs. Shared by ExportAllPngs() and PreviewWindow.BuildPreview().
    public static readonly Dictionary<string, Func<Bitmap?>> ToolbarMap = new()
    {
        ["back"] = ToolbarIcons.Back,
        ["forward"] = ToolbarIcons.Forward,
        ["up"] = ToolbarIcons.Up,
        ["search"] = ToolbarIcons.Search,
        ["folders"] = ToolbarIcons.Folders,
        ["history"] = ToolbarIcons.History,
        ["move-to"] = ToolbarIcons.MoveTo,
        ["copy-to"] = ToolbarIcons.CopyTo,
        ["cut"] = ToolbarIcons.Cut,
        ["copy"] = ToolbarIcons.Copy,
        ["paste"] = ToolbarIcons.Paste,
        ["undo"] = ToolbarIcons.Undo,
        ["delete"] = ToolbarIcons.Delete,
        ["properties"] = ToolbarIcons.Properties,
        ["views"] = ToolbarIcons.Views,
    };

    public static void Main(string[] args)
    {
        var writePngs = args.Contains("--png");
        
        if (writePngs)
        {
            // Initialize Avalonia headless with Skia for PNG export
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            
            ExportAllPngs();
        }
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static void ExportAllPngs()
    {
        var sizes = new[] { 16, 32, 48, 64, 128 };
        // AppContext.BaseDirectory = .../src/Bevel.IconPreview/bin/Debug/net10.0/
        // Need 5 levels up to reach the solution root.
        var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Bevel.Themes.Win2000/Assets/Icons"));
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"  Exporting to: {outDir}");

        foreach (var (name, key) in GlyphMap)
        {
            foreach (var size in sizes)
            {
                var control = Glyphs.GlyphPreview(key, size);
                ExportPng(control, name, size, outDir);
            }
            Console.WriteLine($"  ✓ {name}");
        }

        foreach (var (name, factory) in ToolbarMap)
        {
            var bmp = factory();
            if (bmp != null)
            {
                foreach (var size in sizes)
                {
                    ExportBitmap(bmp, name, size, outDir);
                }
                Console.WriteLine($"  ✓ toolbar/{name}");
            }
        }
    }

    private static void ExportPng(Control control, string name, int size, string outDir)
    {
        var host = new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = control };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));

        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        rtb.Render(host);
        rtb.Save(Path.Combine(outDir, $"{name}-{size}.png"));
    }

    private static void ExportBitmap(Bitmap source, string name, int size, string outDir)
    {
        var scaled = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var img = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
        img.Measure(new Size(size, size));
        img.Arrange(new Rect(0, 0, size, size));
        scaled.Render(img);
        scaled.Save(Path.Combine(outDir, $"toolbar-{name}-{size}.png"));
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .LogToTrace();
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        base.Initialize();
        // Load Win2000 color/brush tokens so Glyphs.ResolveColor() picks up
        // the theme values instead of hardcoded fallback hex.
        var tokens = AvaloniaXamlLoader.Load(
            new Uri("avares://Bevel.Themes.Win2000/Tokens.axaml"));
        if (tokens is ResourceDictionary rd)
            Resources.MergedDictionaries.Add(rd);
    }
}

internal sealed class PreviewWindow : Window
{
    public PreviewWindow()
    {
        Title = "Bevel Win2000 Icon Preview";
        Width = 1000;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#D4D0C8")); // ButtonFace

        Content = BuildPreview();
    }

    private static Control BuildPreview()
    {
        var panel = new WrapPanel { ItemWidth = 140, ItemHeight = 160, Margin = new Avalonia.Thickness(16) };

        // Section: File Types
        panel.Children.Add(SectionHeader("File Types (64×64)"));
        foreach (var (name, key) in Program.GlyphMap)
        {
            var control = Glyphs.GlyphPreview(key, 64);
            panel.Children.Add(IconCard(name, control));
        }

        // Section: Toolbar
        panel.Children.Add(SectionHeader("Toolbar Actions (32×32, rasterized)"));
        foreach (var (name, factory) in Program.ToolbarMap)
        {
            var bmp = factory();
            if (bmp != null)
            {
                var img = new Image { Source = bmp, Width = 32, Height = 32, Stretch = Stretch.Uniform };
                panel.Children.Add(IconCard(name, img));
            }
        }

        // Export button
        var exportBtn = new Button
        {
            Content = "Export PNGs (16/32/48/64/128)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 24, 0, 16),
            Padding = new Avalonia.Thickness(16, 8),
        };
        exportBtn.Click += (_, _) => Program.ExportAllPngs();
        panel.Children.Add(exportBtn);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static Control SectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#0A246A")), // ActiveTitle
            Margin = new Avalonia.Thickness(0, 16, 0, 8)
        };
    }

    private static Control IconCard(string name, Control icon)
    {
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#808080")), // ButtonShadow
            BorderThickness = new Avalonia.Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")), // Window
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Viewbox { Width = 64, Height = 64, Stretch = Stretch.Uniform, Child = icon },
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        TextAlignment = TextAlignment.Center,
                        Foreground = new SolidColorBrush(Color.Parse("#000000")), // WindowText
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
    }
}