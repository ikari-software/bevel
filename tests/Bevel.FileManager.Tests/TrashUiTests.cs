using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-j905: Empty Trash + Restore UI. Controller-level ops run against a constructor-injected
/// temp TrashProvider — never the real ~/.Trash — and menu shape is asserted through the real
/// ContextMenuBuilder.</summary>
public sealed class TrashUiTests : IDisposable
{
    private readonly string _trashDir;
    private readonly string _outsideDir;

    public TrashUiTests()
    {
        _trashDir = Path.Combine(Path.GetTempPath(), $"bevel-trashui-{Guid.NewGuid():N}");
        _outsideDir = Path.Combine(Path.GetTempPath(), $"bevel-trashout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_trashDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_trashDir, true); } catch { }
        try { Directory.Delete(_outsideDir, true); } catch { }
    }

    private (VfsRoot Vfs, FileManagerController Controller) Harness()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        vfs.Register(new TrashProvider(_trashDir));
        var ops = new FileOperationService(vfs, new DefaultConflictHandler());
        return (vfs, new FileManagerController(vfs, ops));
    }

    [Fact]
    public async Task EmptyTrashAsync_deletes_every_item_including_hidden_and_nothing_outside()
    {
        File.WriteAllText(Path.Combine(_trashDir, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_trashDir, ".hidden"), "x");
        Directory.CreateDirectory(Path.Combine(_trashDir, "sub"));
        File.WriteAllText(Path.Combine(_trashDir, "sub", "b.txt"), "x");
        File.WriteAllText(Path.Combine(_outsideDir, "keep.txt"), "x");   // must survive

        var (_, controller) = Harness();
        var result = await controller.EmptyTrashAsync();

        Assert.True(string.IsNullOrEmpty(result.ErrorMessage), result.ErrorMessage);
        Assert.Equal(3, result.SucceededCount);   // a.txt, .hidden, sub
        Assert.Empty(Directory.GetFileSystemEntries(_trashDir));
        Assert.True(File.Exists(Path.Combine(_outsideDir, "keep.txt")));
    }

    [AvaloniaFact]
    public void Folder_menu_offers_empty_trash_only_inside_the_trash()
    {
        MenuItem[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().ToArray();

        var inTrash = ContextMenuBuilder.BuildFolderBackgroundMenu(
            new ContextMenuActions { InTrash = () => true, EmptyTrash = () => { } });
        Assert.Contains(Headers(inTrash), m => (m.Header as string)?.Contains("Empty Trash") == true);

        var outside = ContextMenuBuilder.BuildFolderBackgroundMenu(
            new ContextMenuActions { InTrash = () => false, EmptyTrash = () => { } });
        Assert.DoesNotContain(Headers(outside), m => (m.Header as string)?.Contains("Empty Trash") == true);

        var noFlag = ContextMenuBuilder.BuildFolderBackgroundMenu(new ContextMenuActions { EmptyTrash = () => { } });
        Assert.DoesNotContain(Headers(noFlag), m => (m.Header as string)?.Contains("Empty Trash") == true);
    }

    [AvaloniaFact]
    public void Item_menu_offers_restore_only_for_trash_items()
    {
        var actions = new ContextMenuActions { Restore = () => { } };
        var trashNode = new FakeNode { Path = new VfsPath("trash", "x.txt"), DisplayName = "x.txt" };
        var fileNode = FakeNode.File("a.txt");   // scheme "test"

        var trashMenu = ContextMenuBuilder.BuildItemMenu(trashNode, actions);
        Assert.Contains(trashMenu.Items.OfType<MenuItem>(), m => (m.Header as string)?.Contains("Restore") == true);

        var fileMenu = ContextMenuBuilder.BuildItemMenu(fileNode, actions);
        Assert.DoesNotContain(fileMenu.Items.OfType<MenuItem>(), m => (m.Header as string)?.Contains("Restore") == true);
    }

    [Fact]
    public async Task Restore_moves_the_item_to_the_chosen_folder()
    {
        File.WriteAllText(Path.Combine(_trashDir, "restore.txt"), "data");
        var (_, controller) = Harness();

        var trashItem = VfsPath.Combine(VfsPath.Root("trash"), "restore.txt");
        var result = await controller.MoveAsync(new[] { trashItem }, new VfsPath("file", _outsideDir));

        Assert.True(string.IsNullOrEmpty(result.ErrorMessage), result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(_outsideDir, "restore.txt")));   // arrived
        Assert.False(File.Exists(Path.Combine(_trashDir, "restore.txt")));    // left the trash
    }
}
