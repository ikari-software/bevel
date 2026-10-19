using Bevel.Core;
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Renders paired, fixed-size Flat and Pastel runtime crops (window, controls, start menu, taskbar,
/// and Flat variation set) for visual review against source references and SVG targets.
/// </summary>
[Collection("TaskbarTheme")]
public class RenderFlatAndPastelComparisonTest
{
    /// <summary>
    /// Where the crops land. Defaults to the checked-in design-review set under
    /// docs/design/flat/renders, and <c>BEVEL_FLATPASTEL_DIR</c> redirects it so site/harvest-shots.sh
    /// can take theme-flat.png / theme-pastel.png from the same render the reviewers look at, rather
    /// than someone hand-copying a crop into site/shots and leaving the page to drift from the skin.
    /// </summary>
    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("BEVEL_FLATPASTEL_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../../docs/design/flat/renders"));

    /// <summary>Whether this run is harvesting for the landing page rather than for design review.</summary>
    private static bool ForSite =>
        Environment.GetEnvironmentVariable("BEVEL_FLATPASTEL_DIR") is { Length: > 0 };

    /// <summary>
    /// Writes one crop at the resolution its CONSUMER requires — the two differ, and conflating them
    /// corrupts one of them.
    ///
    /// Design review (the default) stays at 1x because docs/design/flat/look-delta.md verifies these
    /// renders by absolute pixel coordinate — "x=0..3, x=516..519" for the 520-wide window frame,
    /// "clock digits x=866..887" for the 900x30 taskbar — and pins two of them by sha256. Rendering
    /// them at 2x would leave every coordinate in that document quietly wrong.
    ///
    /// The landing page requires 2x: index.html declares the LOGICAL size and site/check-shot-sizes.py
    /// enforces that the file is twice it, so a 1x tile fails that gate and looks smeared beside its
    /// neighbours on a Retina display. <see cref="Bevel.TestSupport.SiteShot"/> also settles the frame
    /// before saving, which the page's byte-comparison drift check needs.
    /// </summary>
    private static void SaveCrop(Window window, string fileName)
    {
        var path = Path.Combine(OutDir, fileName);
        if (ForSite)
        {
            Bevel.TestSupport.SiteShot.Save(window, path);
            return;
        }

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(path);
    }

    private sealed class ComparisonPinnedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 14, 14, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static void EnsureOutDir()
    {
        if (!Directory.Exists(OutDir))
            Directory.CreateDirectory(OutDir);
    }

    [AvaloniaFact]
    public async Task Render_flat_and_pastel_comparisons_to_png()
    {
        EnsureOutDir();

        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Flat);
            Bevel.UI.FlatVariantService.Apply(new BevelSettings { FlatVariant = "Blue" });

            RenderWindowAndControls("flat-window-blue.png", "Home — File Manager");
            await RenderStartMenu("flat-startmenu-blue.png");
            RenderTaskbar("flat-taskbar-blue.png");

            Bevel.UI.FlatVariantService.Apply(new BevelSettings { FlatVariant = "Ergonomic" });
            RenderWindowAndControls("flat-window-ergonomic.png", "Home — Ergonomic");
            RenderTaskbar("flat-taskbar-ergonomic.png");

            Bevel.UI.FlatVariantService.Apply(new BevelSettings { FlatVariant = "Silver" });
            RenderWindowAndControls("flat-window-silver.png", "Home — Silver");
            RenderTaskbar("flat-taskbar-silver.png");

            Bevel.UI.FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" });
            RenderWindowAndControls("flat-window-amber.png", "Home — Amber");
            RenderTaskbar("flat-taskbar-amber.png");
        }
        finally
        {
            Bevel.UI.FlatVariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }

        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Pastel);

            RenderWindowAndControls("pastel-window.png", "Home — Pastel");
            await RenderStartMenu("pastel-startmenu.png");
            RenderTaskbar("pastel-taskbar.png");
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    private static void RenderWindowAndControls(string fileName, string title)
    {
        var body = new StackPanel { Margin = new Thickness(14), Spacing = 10, Width = 480 };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(new Button { Content = "Button", MinWidth = 75, IsDefault = true });
        btnRow.Children.Add(new Button { Content = "Cancel", MinWidth = 75 });
        btnRow.Children.Add(new ToggleButton { Content = "Toggle", IsChecked = true, MinWidth = 75 });
        btnRow.Children.Add(new Button { Content = "Disabled", MinWidth = 75, IsEnabled = false });
        body.Children.Add(btnRow);

        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        checkRow.Children.Add(new CheckBox { Content = "Checked", IsChecked = true });
        checkRow.Children.Add(new CheckBox { Content = "Unchecked", IsChecked = false });
        checkRow.Children.Add(new RadioButton { Content = "Selected", IsChecked = true, GroupName = "G1" });
        checkRow.Children.Add(new RadioButton { Content = "Other", IsChecked = false, GroupName = "G1" });
        body.Children.Add(checkRow);

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        inputRow.Children.Add(new TextBox { Text = "Watercolor field", Width = 180 });
        var combo = new ComboBox { Width = 160 };
        combo.Items.Add("Active item");
        combo.Items.Add("Second item");
        combo.SelectedIndex = 0;
        inputRow.Children.Add(combo);
        body.Children.Add(inputRow);

        body.Children.Add(new ProgressBar { Value = 62, Minimum = 0, Maximum = 100, Height = 14 });
        body.Children.Add(new Slider { Value = 55, Minimum = 0, Maximum = 100, Width = 240, HorizontalAlignment = HorizontalAlignment.Left });

        var tabs = new TabControl { Height = 80 };
        tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "Watercolor tab content", Margin = new Thickness(6) } });
        tabs.Items.Add(new TabItem { Header = "Appearance" });
        tabs.Items.Add(new TabItem { Header = "Settings" });
        body.Children.Add(tabs);

        var window = new Bevel.UI.BevelWindow
        {
            Title = title,
            Width = 520,
            Height = 360,
            Content = body,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        SaveCrop(window, fileName);
        window.Close();
    }

    private static async Task RenderStartMenu(string fileName)
    {
        var appEnv = new HeroAppEnvironment();
        using var model = new ShellModel(null, appEnv, new HeroIconProvider(), usage: TestUsage.Scratch());
        model.Start();
        var vm = new StartMenuViewModel(model);
        for (var i = 0; i < 50 && model.Programs.Count < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        var menu = new StartMenu(null, null, programs: vm);
        var button = new Button { Content = "Start" };
        var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        await menu.OpenAsync(button);
        Dispatcher.UIThread.RunJobs();
        menu.Close();
        host.Close();

        var content = (Control)menu.MenuPopupControl.Child!;
        menu.MenuPopupControl.Child = null;

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = content,
        };
        window.Show();

        Avalonia.Media.Imaging.WriteableBitmap? frame = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            frame = window.CaptureRenderedFrame();
            if (frame is not null && frame.PixelSize.Width >= 340) break;
            await Task.Delay(10);
        }

        Assert.NotNull(frame);
        frame!.Save(Path.Combine(OutDir, fileName));
        window.Close();
    }

    private static void RenderTaskbar(string fileName)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWindowManager();
        var view = new TaskbarView { DataContext = vm };
        var window = new Window { SystemDecorations = SystemDecorations.None, Width = 900, Height = 30, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 2; i++)
        {
            var fw = new ForeignWindow(new ForeignWindowId($"w{i}"), $"Window {i + 1}", "App", false, i == 0, default);
            model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        }
        Dispatcher.UIThread.RunJobs();

        foreach (var clock in window.GetVisualDescendants().OfType<ClockWidget>())
            clock.Time = new ComparisonPinnedClock();
        Dispatcher.UIThread.RunJobs();

        SaveCrop(window, fileName);
        window.Close();
    }
}
