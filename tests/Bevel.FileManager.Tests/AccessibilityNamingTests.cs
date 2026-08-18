using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// bevel-6zs6: the Explorer's icon-only controls carry explicit screen-reader names (ToolTip.Tip is
/// NOT surfaced as the accessible name). These pin that every toolbar button, the address field, and
/// each tab's close button announce something meaningful instead of "button".
/// </summary>
public class AccessibilityNamingTests
{
    [AvaloniaFact]
    public void Every_toolbar_button_has_an_accessible_name()
    {
        var toolbar = new Toolbar();
        new Window { Content = toolbar }.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(toolbar)
            .OfType<Classic.CommonControls.ToolBarButton>().ToList();
        Assert.NotEmpty(buttons);
        foreach (var b in buttons)
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)),
                $"a toolbar button is missing an AutomationProperties.Name");
    }

    [AvaloniaFact]
    public void Tab_and_its_close_button_are_named_and_track_the_header()
    {
        var strip = new TabStrip();
        new Window { Content = strip }.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var id = strip.AddTab("Documents");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var close = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(strip)
            .OfType<Button>().First(b => (b.Content as string) == "×");
        Assert.Equal("Close tab Documents", AutomationProperties.GetName(close));

        strip.SetHeader(id, "Pictures");
        Assert.Equal("Close tab Pictures", AutomationProperties.GetName(close));
    }
}
