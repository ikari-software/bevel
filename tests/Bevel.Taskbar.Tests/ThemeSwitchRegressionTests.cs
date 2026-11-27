using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Live theme switching, cycled the way a user clicks through the Appearance tab. Two owner-reported
/// regressions: the Start badge vanished after a few switches, and Filer window borders turned black
/// on Flat/Pastel once Industrial1999 had been visited.
/// </summary>
[Collection("TaskbarTheme")]   // swaps the app-wide theme
public class ThemeSwitchRegressionTests
{
    static readonly string[] Cycle =
    {
        ThemeIds.Blue2001, ThemeIds.Flat, ThemeIds.Pastel, ThemeIds.Industrial1999,
        ThemeIds.Blue2001, ThemeIds.Flat, ThemeIds.Industrial1999, ThemeIds.Pastel,
    };

    /// <summary>What App.axaml.cs does on a settings change: the template swap, then the theme's variant.</summary>
    static void Switch(BevelSettings s, string id)
    {
        s.ThemeId = id;
        if (Bevel.UI.ThemeService.Apply(id)) Bevel.UI.ThemeVariants.Apply(s);
        Dispatcher.UIThread.RunJobs();
    }

    static void BackToDefault(BevelSettings s) => Switch(s, ThemeIds.Industrial1999);

    [AvaloniaFact]
    public void Start_badge_survives_a_cycle_of_theme_switches()
    {
        var s = new BevelSettings();
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var view = new TaskbarView { DataContext = new TaskbarViewModel(model, new StartMenuViewModel(model)) };
        view.Initialize(TestSettings.NoStacks(new BevelSettings()));
        var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var host = view.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "StartLogoHost");
            foreach (var id in Cycle)
            {
                Switch(s, id);
                var badge = Assert.IsAssignableFrom<Control>(host.Content);
                Assert.True(ReferenceEquals(host.Presenter?.Child, badge),
                    $"after switching to {id} the badge is not in the presenter (child = {host.Presenter?.Child?.GetType().Name ?? "null"})");
                Assert.True(badge.Bounds.Width > 0, $"after switching to {id} the badge has no size ({badge.Bounds})");
            }
        }
        finally
        {
            window.Close();
            BackToDefault(s);
        }
    }

    static Color WindowFrame() =>
        Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.FindResource(Bevel.UI.ThemeTokens.BrushWindowFrame)).Color;

    [AvaloniaTheory]
    [InlineData(ThemeIds.Flat)]
    [InlineData(ThemeIds.Pastel)]
    public void Theme_palette_is_the_same_after_visiting_industrial1999(string theme)
    {
        var s = new BevelSettings();
        try
        {
            Switch(s, theme);
            var fresh = WindowFrame();
            Assert.NotEqual(Colors.Black, fresh);

            Switch(s, ThemeIds.Industrial1999);   // merges the colour-scheme alias layer
            Switch(s, theme);

            Assert.Equal(fresh, WindowFrame());
        }
        finally { BackToDefault(s); }
    }

    [AvaloniaFact]
    public void Window_frame_border_follows_the_theme_after_visiting_industrial1999()
    {
        var s = new BevelSettings();
        var win = new Bevel.UI.BevelWindow { Width = 300, Height = 200, Title = "Frame", Content = new TextBlock { Text = "x" } };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Switch(s, ThemeIds.Flat);
            Switch(s, ThemeIds.Industrial1999);
            Switch(s, ThemeIds.Flat);
            var framed = win.GetVisualDescendants().OfType<Border>()
                .Where(b => b.BorderThickness != default && b.BorderBrush is ISolidColorBrush)
                .Select(b => ((ISolidColorBrush)b.BorderBrush!).Color).ToList();
            Assert.NotEmpty(framed);
            Assert.DoesNotContain(Colors.Black, framed);
        }
        finally
        {
            win.Close();
            BackToDefault(s);
        }
    }
}
