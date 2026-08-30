using System.Linq;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
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

    /// <summary>bevel-6zs6: the file list was a bare ItemsControl of non-control template elements — no
    /// list, no item names, no selected state reached a screen reader. Each realized row now carries a
    /// ListItem automation role, an accessible name (its DisplayName), and a SelectionItem pattern that
    /// mirrors IsSelected. Pins all three on the realized rows, including the selected one.</summary>
    [AvaloniaFact]
    public void ItemView_rows_expose_listitem_name_and_selected_state()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Window { Content = view, Width = 400, Height = 300 };
        w.Show();

        view.Items = new[] { Node("alpha.txt"), Node("beta.txt"), Node("gamma.txt") };
        Dispatcher.UIThread.RunJobs();
        w.MouseMove(new Avalonia.Point(1, 1));   // nudge a layout pass so the rows realize
        Dispatcher.UIThread.RunJobs();
        view.SelectPath(new VfsPath("test", "beta.txt"));
        Dispatcher.UIThread.RunJobs();

        var rows = view.GetVisualDescendants().OfType<ItemRow>().ToList();
        Assert.Equal(3, rows.Count);

        foreach (var row in rows)
        {
            var vm = Assert.IsType<ItemViewModel>(row.DataContext);

            // The accessible name is present and equals the file's display name (not a tooltip, not blank).
            var attachedName = AutomationProperties.GetName(row);
            Assert.False(string.IsNullOrWhiteSpace(attachedName));
            Assert.Equal(vm.DisplayName, attachedName);

            // The peer reports a ListItem role, the same name, and its selected state tracks the row.
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
            Assert.Equal(vm.DisplayName, peer.GetName());

            var selItem = Assert.IsAssignableFrom<ISelectionItemProvider>(peer);
            Assert.Equal(vm.DisplayName == "beta.txt", selItem.IsSelected);
        }

        // The container itself announces as a List so the rows sit under a proper list.
        var listPeer = ControlAutomationPeer.CreatePeerForElement(view.ItemsControl);
        Assert.Equal(AutomationControlType.List, listPeer.GetAutomationControlType());
    }

    private static IVfsNode Node(string name)
        => new A11yNode
        {
            Path = new VfsPath("test", name),
            DisplayName = name,
            Kind = VfsNodeKind.File,
            Modified = System.DateTimeOffset.UtcNow,
            TypeDescription = "Text Document",
        };
}

file sealed class A11yNode : IVfsNode
{
    public VfsPath Path { get; init; }
    public string DisplayName { get; init; } = "";
    public VfsNodeKind Kind { get; init; }
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public System.DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }
}
