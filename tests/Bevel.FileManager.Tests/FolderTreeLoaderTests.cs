using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;
using static Bevel.FileManager.Tests.DispatcherPump;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-so27 review #3/#5: the shared tree loader's failure, cancellation and
/// expand-after-completion paths, plus discarding a batch already handed over when the token is
/// cancelled.</summary>
public sealed class FolderTreeLoaderTests
{
    private readonly RecordingVfsProvider _provider = new();
    private readonly VfsRoot _root = new();

    public FolderTreeLoaderTests() => _root.Register(_provider);

    private static TreeViewItem NodeFor(string folder)
    {
        var node = new TreeViewItem { Tag = RecordingVfsProvider.Path(folder) };
        node.Items.Add(FolderTreeLoader.Placeholder());
        return node;
    }

    private static TreeViewItem Child(IVfsNode n) => new() { Header = n.DisplayName, Tag = n.Path };

    private Task Expand(TreeViewItem node, CancellationToken ct = default)
        => FolderTreeLoader.PopulateAsync(node, _root, new EnumerateOptions(), Child, ct);

    [AvaloniaFact]
    public void A_folder_that_fails_to_enumerate_is_left_empty_without_throwing()
    {
        var node = NodeFor("/fail");

        PumpUntil(Expand(node));   // PumpUntil rethrows a faulted task

        Assert.Empty(node.Items);   // not stuck on its placeholder
    }

    [AvaloniaFact]
    public void A_cancelled_expansion_adds_nothing_and_does_not_throw()
    {
        var node = NodeFor("/top");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        PumpUntil(Expand(node, cts.Token));

        Assert.Empty(node.Items);
    }

    [AvaloniaFact]
    public void Expanding_again_after_the_load_finished_neither_rejoins_nor_duplicates()
    {
        var node = NodeFor("/top");
        PumpUntil(Expand(node));
        Assert.Equal(40, node.Items.Count);

        var again = Expand(node);
        Assert.True(again.IsCompleted);   // already populated: no new load, no dead task re-joined
        PumpUntil(again);
        Assert.Equal(40, node.Items.Count);
        Assert.Equal(1, _provider.Enumerations("/top"));

        // An EMPTY folder has no children to say "already populated", so a later expand must start a
        // fresh read — not hand back the finished one from the in-flight table.
        var empty = NodeFor("/empty");
        PumpUntil(Expand(empty));
        PumpUntil(Expand(empty));
        Assert.Equal(2, _provider.Enumerations("/empty"));
    }

    [AvaloniaFact]
    public void A_batch_already_handed_over_when_the_token_is_cancelled_is_not_added()
    {
        // Both "/staledirs" batches are in the reader's channel before the UI consumes any. While the
        // FIRST is added, a Background job cancels; the reader hands the SECOND over (the token was
        // still live when it did), and only the loader's post-await check keeps it out of the tree.
        var node = NodeFor("/staledirs");
        using var cts = new CancellationTokenSource();
        var created = 0;
        var load = FolderTreeLoader.PopulateAsync(node, _root, new EnumerateOptions(), n =>
        {
            if (created++ == 0) Dispatcher.UIThread.Post(cts.Cancel, DispatcherPriority.Background);
            return Child(n);
        }, cts.Token);
        _provider.WaitEnded("/staledirs");

        PumpUntil(load);

        Assert.Equal(FolderTreeLoader.BatchSize, node.Items.Count);
    }
}
