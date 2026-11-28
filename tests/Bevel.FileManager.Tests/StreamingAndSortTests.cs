using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

public class StreamingAndSortTests
{
    static readonly string TmpDir = Path.Combine(Path.GetTempPath(), "bevel-test-" + Guid.NewGuid().ToString("N")[..8]);

    static StreamingAndSortTests()
    {
        Directory.CreateDirectory(TmpDir);
        for (int i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(TmpDir, $"file_{i:D3}.txt"), $"content {i}");
        Directory.CreateDirectory(Path.Combine(TmpDir, "subdir"));
    }

    static void PumpAndShow(Avalonia.Controls.Control view, int w = 600, int h = 400)
    {
        new Avalonia.Controls.Window { Content = view, Width = w, Height = h }.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Streaming_add_items_while_view_is_active_does_not_crash()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var path = new VfsPath("file", TmpDir);

        view.ResetItems();
        var batch = new List<IVfsNode>();
        var ct = CancellationToken.None;
        var enumerator = vfs.EnumerateAsync(path, new EnumerateOptions(), ct).GetAsyncEnumerator(ct);

        int total = 0;
        while (enumerator.MoveNextAsync().AsTask().Result)
        {
            batch.Add(enumerator.Current);
            total++;
            if (batch.Count >= 10)
            {
                view.AddItems(batch.ToArray());
                batch.Clear();
                Dispatcher.UIThread.RunJobs();
            }
        }
        if (batch.Count > 0) view.AddItems(batch.ToArray());
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(total, src.Count);
    }

    [AvaloniaFact]
    public void Sort_after_streaming_while_virtualized_does_not_crash()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        var nodes = Enumerable.Range(0, 200)
            .Select(i => TestNode.Make($"file_{i:D3}.txt"))
            .ToArray();
        view.ResetItems();
        view.AddItems(nodes);
        Dispatcher.UIThread.RunJobs();

        // Trigger sort — this was the Clear+Virtualization crash
        view.AddItems(Array.Empty<IVfsNode>());
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(200, src.Count);
    }

    [AvaloniaFact]
    public void View_mode_switch_after_streamed_items_does_not_crash()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        var nodes = Enumerable.Range(0, 50)
            .Select(i => TestNode.Make($"file_{i:D3}.txt"))
            .ToArray();
        view.ResetItems();
        view.AddItems(nodes);
        Dispatcher.UIThread.RunJobs();

        view.ViewMode = ViewMode.LargeIcons; Dispatcher.UIThread.RunJobs();
        view.ViewMode = ViewMode.SmallIcons; Dispatcher.UIThread.RunJobs();
        view.ViewMode = ViewMode.List;       Dispatcher.UIThread.RunJobs();
        view.ViewMode = ViewMode.Details;    Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(50, src.Count);
    }

    [AvaloniaFact]
    public void Batch_merge_keeps_items_sorted_across_many_batches()   // bevel-onyp item 2
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.ResetItems();

        // 1000 items streamed in 32-item batches, names in random order.
        var rng = new Random(42);
        var all = Enumerable.Range(0, 1000)
            .Select(i => TestNode.Make($"file_{i:D4}.txt"))
            .OrderBy(_ => rng.Next())
            .ToArray();
        foreach (var batch in all.Chunk(32))
            view.AddItems(batch);
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        var names = src.Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray();
        Assert.Equal(1000, names.Length);
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, names);   // fully sorted after all batches
    }

    [AvaloniaFact]
    public void Batch_merge_matches_per_item_insert_order()   // bevel-onyp item 2: stability parity
    {
        // Equal sort keys (same name, different size) must land in the same relative order
        // as the old per-item InsertSorted did: batch item AFTER existing equal key.
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.ResetItems();

        var a = TestNode.Make("same.txt", size: 1);
        var b = TestNode.Make("same.txt", size: 2);
        var c = TestNode.Make("same.txt", size: 3);
        view.AddItems(new[] { a });
        view.AddItems(new[] { b, c });   // batch: b then c, both after a
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        var sizes = src.Cast<ItemViewModel>().Select(v => v.Size).ToArray();
        Assert.Equal(new long?[] { 1, 2, 3 }, sizes);
    }
}

file sealed class TestNode : IVfsNode
{
    public VfsPath Path { get; init; }
    public string DisplayName { get; init; } = "";
    public VfsNodeKind Kind { get; init; }
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }

    public static IVfsNode Make(string name, VfsNodeKind kind = VfsNodeKind.File, long? size = 1024)
        => new TestNode
        {
            Path = new VfsPath("test", name), DisplayName = name, Kind = kind, Size = size,
            Modified = DateTimeOffset.UtcNow,
            TypeDescription = kind == VfsNodeKind.Folder ? "File Folder" : "Text Document",
            MightHaveChildren = kind == VfsNodeKind.Folder,
        };
}