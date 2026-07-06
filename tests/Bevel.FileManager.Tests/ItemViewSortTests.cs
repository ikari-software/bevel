using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests for ItemView sorting (bevel-rn9): the shared OrderItems logic, and the async
/// (large-list) sort path preserving every item without a virtualization crash.
/// </summary>
public class ItemViewSortTests
{
    private static ItemViewModel Vm(string name, long size = 0, string type = "File", DateTimeOffset? modified = null)
        => new(FakeNode.File(name, size, type, modified ?? DateTimeOffset.UnixEpoch));

    [Fact]
    public void OrderItems_sorts_by_name()
    {
        var b = Vm("banana"); var a = Vm("apple"); var c = Vm("cherry");
        Assert.Equal(new[] { a, b, c }, ItemView.OrderItems(new[] { b, a, c }, ItemView.SortColumn.Name, asc: true));
        Assert.Equal(new[] { c, b, a }, ItemView.OrderItems(new[] { b, a, c }, ItemView.SortColumn.Name, asc: false));
    }

    [Fact]
    public void OrderItems_sorts_by_size()
    {
        var big = Vm("a", size: 30); var small = Vm("b", size: 10); var mid = Vm("c", size: 20);
        Assert.Equal(new[] { small, mid, big }, ItemView.OrderItems(new[] { big, small, mid }, ItemView.SortColumn.Size, asc: true));
        Assert.Equal(new[] { big, mid, small }, ItemView.OrderItems(new[] { big, small, mid }, ItemView.SortColumn.Size, asc: false));
    }

    [Fact]
    public void OrderItems_sorts_by_type_then_modified()
    {
        var zType = Vm("a", type: "Zip"); var aType = Vm("b", type: "Application");
        Assert.Equal(new[] { aType, zType }, ItemView.OrderItems(new[] { zType, aType }, ItemView.SortColumn.Type, asc: true));

        var older = Vm("a", modified: DateTimeOffset.UnixEpoch);
        var newer = Vm("b", modified: DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(new[] { older, newer }, ItemView.OrderItems(new[] { newer, older }, ItemView.SortColumn.Modified, asc: true));
    }

    [AvaloniaFact]
    public void Async_sort_of_large_list_preserves_every_item()
    {
        // The async path triggers for >= 500 items. It must keep all items (no dropped
        // late arrivals) and not crash on the ItemsSource swap.
        var view = new ItemView { ViewMode = ViewMode.Details };
        new Window { Content = view, Width = 600, Height = 400 }.Show();
        Dispatcher.UIThread.RunJobs();

        var nodes = Enumerable.Range(0, 600).Select(i => FakeNode.File($"file_{i:D4}.txt")).ToArray();
        view.ResetItems();
        view.AddItems(nodes);
        Dispatcher.UIThread.RunJobs();

        var sortAsync = typeof(ItemView).GetMethod("SortAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)sortAsync.Invoke(view, null)!;

        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.ElapsedMilliseconds < 5000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();

        Assert.True(task.IsCompletedSuccessfully, "SortAsync did not complete successfully");
        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(600, src.Count);
    }
}
