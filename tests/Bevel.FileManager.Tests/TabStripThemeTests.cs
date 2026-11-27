using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.FileManager.Components;
using Bevel.UI;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Filer tab colours are theme tokens, not brush objects baked at type-init (bevel-5rel): a live theme or
/// colour-scheme switch recolours the active tab, the inactive tabs and the tab outlines in place.
/// </summary>
[Collection("TaskbarTheme")]   // overrides Application-level brush tokens
public sealed class TabStripThemeTests
{
    static (TabStrip Strip, Border Active, Border Inactive) TwoTabs()
    {
        var strip = new TabStrip();
        new Window { Content = strip, Width = 400, Height = 60 }.Show();
        Dispatcher.UIThread.RunJobs();
        var first = strip.AddTab("one");
        strip.AddTab("two");
        strip.SetActive(first);
        Dispatcher.UIThread.RunJobs();
        var tabs = strip.GetVisualDescendants().OfType<Border>()
            .Where(b => Avalonia.Automation.AutomationProperties.GetName(b) is "one" or "two").ToList();
        return (strip, tabs.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "one"),
                       tabs.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "two"));
    }

    static void WithToken(string key, IBrush brush, Action body)
    {
        var resources = Application.Current!.Resources;
        bool had = resources.TryGetValue(key, out var old);
        resources[key] = brush;
        try { Dispatcher.UIThread.RunJobs(); body(); }
        finally
        {
            if (had) resources[key] = old; else resources.Remove(key);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Active_tab_follows_the_window_token()
    {
        var (_, active, _) = TwoTabs();
        var red = new SolidColorBrush(Colors.Red);
        WithToken(ThemeTokens.BrushWindow, red, () => Assert.Same(red, active.Background));
    }

    [AvaloniaFact]
    public void Inactive_tab_follows_the_button_face_token()
    {
        var (_, _, inactive) = TwoTabs();
        var red = new SolidColorBrush(Colors.Red);
        WithToken(ThemeTokens.BrushButtonFace, red, () => Assert.Same(red, inactive.Background));
    }

    [AvaloniaFact]
    public void Tab_outline_follows_the_button_shadow_token()
    {
        var (_, active, _) = TwoTabs();
        var red = new SolidColorBrush(Colors.Red);
        WithToken(ThemeTokens.BrushButtonShadow, red, () => Assert.Same(red, active.BorderBrush));
    }

    [AvaloniaFact]
    public void Switching_tabs_swaps_the_two_tokens()
    {
        var (strip, active, inactive) = TwoTabs();
        var w = new SolidColorBrush(Colors.Red);
        var f = new SolidColorBrush(Colors.Blue);
        WithToken(ThemeTokens.BrushWindow, w, () => WithToken(ThemeTokens.BrushButtonFace, f, () =>
        {
            Assert.Same(w, active.Background);
            Assert.Same(f, inactive.Background);
        }));
    }
}
