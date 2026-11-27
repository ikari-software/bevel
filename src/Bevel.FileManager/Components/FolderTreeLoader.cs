using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// Lazy first-expand population shared by the Filer's folder tree and the Browse-For-Folder tree
/// (bevel-so27). A node carries one <c>"..."</c> placeholder child until it is expanded; expanding
/// swaps it for the node's folder children (folders, volumes and virtual roots — never files).
///
/// <para>The listing is read on the pool by <see cref="VfsBatchReader"/> and added here, on the UI
/// thread, one batch per dispatcher turn (an explicit Background-priority yield before each batch). Because the work is now genuinely asynchronous, a node can be
/// expanded again (collapse + re-expand) while its first load is still in flight; that second call
/// joins the first load rather than starting a second enumeration that would append duplicates.</para>
/// </summary>
internal static class FolderTreeLoader
{
    public const string PlaceholderHeader = "...";
    public const int BatchSize = 32;

    /// <summary>The load currently populating each node, so a repeated expand joins it.</summary>
    private static readonly ConditionalWeakTable<TreeViewItem, Task> InFlight = new();

    public static TreeViewItem Placeholder() => new() { Header = PlaceholderHeader };

    /// <summary>Populates <paramref name="node"/> (tagged with its <see cref="VfsPath"/>) on first
    /// expand. The returned task completes when the listing has landed, failed (the node is left
    /// empty — best effort, as before) or been cancelled; expanding a node whose load is still running
    /// returns that same load. Must be called on the UI thread.</summary>
    public static Task PopulateAsync(
        TreeViewItem node,
        VfsRoot root,
        EnumerateOptions options,
        Func<IVfsNode, TreeViewItem> createChild,
        CancellationToken ct)
    {
        if (node.Tag is not VfsPath path) return Task.CompletedTask;
        if (InFlight.TryGetValue(node, out var pending) && !pending.IsCompleted) return pending;

        if (node.Items.Count == 1 && node.Items[0] is TreeViewItem { Header: PlaceholderHeader })
            node.Items.Clear();
        else if (node.Items.Count > 0)
            return Task.CompletedTask; // already populated

        var load = LoadAsync(node, root, path, options, createChild, ct);
        if (!load.IsCompleted) InFlight.AddOrUpdate(node, load);
        return load;
    }

    private static async Task LoadAsync(
        TreeViewItem node,
        VfsRoot root,
        VfsPath path,
        EnumerateOptions options,
        Func<IVfsNode, TreeViewItem> createChild,
        CancellationToken ct)
    {
        try
        {
            await foreach (var batch in VfsBatchReader.ReadBatchesAsync(root, path, options, BatchSize, ct))
            {
                // A real dispatcher turn per batch: when the reader is ahead, buffered batches are handed
                // over synchronously and would otherwise all be added inside one job.
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                // The tree may have been torn down (or the token cancelled) while this batch waited.
                if (ct.IsCancellationRequested) return;
                foreach (var child in batch)
                {
                    if (child.Kind is not (VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot))
                        continue;
                    node.Items.Add(createChild(child));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Root changed or window closing — stop populating.
        }
        catch
        {
            // Best-effort: a folder we can't enumerate (permissions, races, a vanished volume) is
            // left with no children rather than stuck on its placeholder.
        }
        finally
        {
            InFlight.Remove(node);
        }
    }
}
