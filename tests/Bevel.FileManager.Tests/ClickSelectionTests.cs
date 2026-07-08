using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>Clicking an item with the mouse must select it — in every view mode.</summary>
public sealed class ClickSelectionTests
{
    private static IVfsNode Node(string name)
        => new ClickNode { Path = new VfsPath("test", name), DisplayName = name, Kind = VfsNodeKind.File };

    private static void ClickSelects(ViewMode mode, bool rtl = false)
    {
        var view = new ItemView { ViewMode = mode };
        var w = new Window { Content = view, Width = 500, Height = 400 };
        if (rtl) w.FlowDirection = FlowDirection.RightToLeft;   // ambient reading order -> mirrored layout
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();
        w.MouseMove(new Point(1, 1));          // nudge a layout/render pass
        Dispatcher.UIThread.RunJobs();

        var target = view.ItemsControl.GetRealizedContainers()
            .First(c => (c.DataContext as ItemViewModel)?.DisplayName == "b.txt");
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;

        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b.txt", view.SelectedItems.Single().DisplayName);
    }

    [AvaloniaFact] public void Details_click_selects() => ClickSelects(ViewMode.Details);
    [AvaloniaFact] public void LargeIcons_click_selects() => ClickSelects(ViewMode.LargeIcons);
    [AvaloniaFact] public void List_click_selects() => ClickSelects(ViewMode.List);
    [AvaloniaFact] public void LargeIcons_click_selects_under_rtl() => ClickSelects(ViewMode.LargeIcons, rtl: true);
}

file sealed class ClickNode : IVfsNode
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
    public System.Collections.Generic.IReadOnlyDictionary<string, object?> ExtraColumns { get; init; }
        = new System.Collections.Generic.Dictionary<string, object?>();
}
