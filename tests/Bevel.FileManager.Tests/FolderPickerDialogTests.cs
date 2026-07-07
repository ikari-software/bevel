using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests for the "Browse For Folder" modal backing File&gt;Move To Folder / Copy To Folder.
/// Mirrors FileManagerWindowTreeTests: lazy expansion is driven directly (internal methods,
/// visible via InternalsVisibleTo) rather than relying on the visual Expanded/SelectionChanged
/// events firing in a headless run, and OK confirmation is checked without a real modal loop.
/// </summary>
public sealed class FolderPickerDialogTests : IDisposable
{
    private readonly string _dir;

    public FolderPickerDialogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-folderpicker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "SubFolder"));
        File.WriteAllText(Path.Combine(_dir, "note.txt"), "not a folder");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static VfsRoot NewVfsRoot()
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        return root;
    }

    /// <summary>Drives lazy expansion of a node the same way FileManagerWindowTreeTests does:
    /// invoke the handler, then pump the dispatcher until the placeholder resolves.</summary>
    private static void Expand(FolderPickerDialog dialog, TreeViewItem node)
    {
        dialog.OnTreeNodeExpanded(node, new RoutedEventArgs());

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000
               && node.Items.Count == 1
               && node.Items[0] is TreeViewItem p && (p.Header as string) == "...")
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Root_node_is_created_expanded_and_tagged_with_the_starting_path()
    {
        var dialog = new FolderPickerDialog(NewVfsRoot(), new VfsPath("file", _dir));

        var root = Assert.IsType<TreeViewItem>(dialog.Tree.Items.Single());
        Assert.True(root.IsExpanded);
        Assert.Equal(_dir, ((VfsPath)root.Tag!).Value);
    }

    [AvaloniaFact]
    public void Expanding_root_lazily_populates_only_folder_children()
    {
        var dialog = new FolderPickerDialog(NewVfsRoot(), new VfsPath("file", _dir));
        var root = (TreeViewItem)dialog.Tree.Items.Single()!;

        Expand(dialog, root);

        var children = root.Items.OfType<TreeViewItem>().Select(t => t.Header as string ?? "").ToList();
        Assert.Contains("SubFolder", children);
        Assert.DoesNotContain("note.txt", children); // files are excluded from the folder picker
    }

    [AvaloniaFact]
    public void No_selection_disables_ok_and_confirm_returns_null()
    {
        var dialog = new FolderPickerDialog(NewVfsRoot(), new VfsPath("file", _dir));

        Assert.False(dialog.Ok.IsEnabled);
        Assert.Null(dialog.ConfirmSelection());
    }

    [AvaloniaFact]
    public void Selecting_a_folder_enables_ok_and_confirm_yields_its_path()
    {
        var dialog = new FolderPickerDialog(NewVfsRoot(), new VfsPath("file", _dir));
        var root = (TreeViewItem)dialog.Tree.Items.Single()!;
        Expand(dialog, root);
        var subFolder = root.Items.OfType<TreeViewItem>().Single(t => (t.Header as string) == "SubFolder");

        dialog.SelectNode(subFolder);

        Assert.True(dialog.Ok.IsEnabled);
        var confirmed = dialog.ConfirmSelection();
        Assert.NotNull(confirmed);
        Assert.Equal(Path.Combine(_dir, "SubFolder"), confirmed!.Value.Value);
        Assert.Equal("file", confirmed.Value.Scheme);
    }

    [AvaloniaFact]
    public void Deselecting_disables_ok_and_confirm_returns_null_again()
    {
        var dialog = new FolderPickerDialog(NewVfsRoot(), new VfsPath("file", _dir));
        var root = (TreeViewItem)dialog.Tree.Items.Single()!;
        Expand(dialog, root);
        var subFolder = root.Items.OfType<TreeViewItem>().Single(t => (t.Header as string) == "SubFolder");

        dialog.SelectNode(subFolder);
        Assert.True(dialog.Ok.IsEnabled);

        dialog.SelectNode(null);

        Assert.False(dialog.Ok.IsEnabled);
        Assert.Null(dialog.ConfirmSelection());
    }
}
