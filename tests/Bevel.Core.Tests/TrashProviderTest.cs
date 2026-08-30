using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-sw3k: the Trash must be browsable (selecting the trash:// node used to throw
/// KeyNotFoundException because no provider was registered), and its items must support a PERMANENT
/// delete (they're already trashed) plus restore-by-move — but never rename/re-trash.</summary>
public class TrashProviderTest : IDisposable
{
    private readonly string _trashDir;
    private readonly TrashProvider _provider;

    public TrashProviderTest()
    {
        _trashDir = Path.Combine(Path.GetTempPath(), "bevel-trash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_trashDir);
        _provider = new TrashProvider(_trashDir);
    }

    public void Dispose() { try { Directory.Delete(_trashDir, true); } catch { } }

    private async Task<List<IVfsNode>> EnumerateRoot()
    {
        var items = new List<IVfsNode>();
        await foreach (var n in _provider.EnumerateAsync(VfsPath.Root("trash"), new EnumerateOptions(), default))
            items.Add(n);
        return items;
    }

    [Fact]
    public async Task Enumerates_items_and_skips_macos_bookkeeping()
    {
        File.WriteAllText(Path.Combine(_trashDir, "deleted.txt"), "gone");
        Directory.CreateDirectory(Path.Combine(_trashDir, "oldfolder"));
        File.WriteAllText(Path.Combine(_trashDir, ".DS_Store"), "noise");

        var names = new HashSet<string>();
        foreach (var n in await EnumerateRoot()) names.Add(n.DisplayName);

        Assert.Contains("deleted.txt", names);
        Assert.Contains("oldfolder", names);
        Assert.DoesNotContain(".DS_Store", names);   // bookkeeping is filtered out
    }

    [Fact]
    public async Task Root_resolves_as_a_virtual_root_named_Trash()
    {
        File.WriteAllText(Path.Combine(_trashDir, "x.txt"), "x");

        var root = await _provider.ResolveAsync(VfsPath.Root("trash"), default);

        Assert.Equal(VfsNodeKind.VirtualRoot, root.Kind);
        Assert.Equal("Trash", root.DisplayName);
        Assert.Equal("trash.full", root.IconKey.SemanticId);   // has items → full basket
    }

    [Fact]
    public async Task Items_allow_permanent_delete_but_not_rename_or_retrash()
    {
        File.WriteAllText(Path.Combine(_trashDir, "item.txt"), "x");
        var node = Assert.Single(await EnumerateRoot());

        Assert.True(node.Caps.HasFlag(VfsCapabilities.Delete));
        Assert.True(node.Caps.HasFlag(VfsCapabilities.CopySource));
        Assert.False(node.Caps.HasFlag(VfsCapabilities.Trash));    // can't re-trash the trash
        Assert.False(node.Caps.HasFlag(VfsCapabilities.Rename));   // can't rename in place
    }

    [Fact]
    public async Task DeleteAsync_permanently_removes_the_item()
    {
        var path = Path.Combine(_trashDir, "doomed.txt");
        File.WriteAllText(path, "x");
        var mutator = await _provider.GetMutatorAsync(VfsPath.Root("trash"), default);
        Assert.NotNull(mutator);

        var itemPath = VfsPath.Combine(VfsPath.Root("trash"), "doomed.txt");
        var restore = await mutator!.DeleteAsync(itemPath, toTrash: true, default);  // toTrash ignored

        Assert.Null(restore);                 // nothing to restore — it's a permanent delete
        Assert.False(File.Exists(path));      // and it's actually gone from disk
    }

    [Fact]
    public void ResolveEffectivePath_hides_the_root_but_maps_leaves_natively()
    {
        // Root stays in the trash:// namespace (null) so it doesn't navigate to the raw ~/.Trash folder…
        Assert.Null(_provider.ResolveEffectivePath(VfsPath.Root("trash")));

        // …while a leaf resolves to its real on-disk path so preview/open/copy work.
        var leaf = VfsPath.Combine(VfsPath.Root("trash"), "a.txt");
        Assert.Equal(Path.Combine(_trashDir, "a.txt"), _provider.ResolveEffectivePath(leaf));
    }

    [Fact]
    public async Task MoveAsync_restores_an_item_out_to_a_chosen_folder()
    {
        var trashed = Path.Combine(_trashDir, "restoreme.txt");
        File.WriteAllText(trashed, "back");
        var destDir = Path.Combine(Path.GetTempPath(), "bevel-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destDir);
        try
        {
            var mutator = await _provider.GetMutatorAsync(VfsPath.Root("trash"), default);
            var itemPath = VfsPath.Combine(VfsPath.Root("trash"), "restoreme.txt");

            var newPath = await mutator!.MoveAsync(itemPath, new VfsPath("file", destDir), default);

            Assert.Equal("file", newPath.Scheme);
            Assert.True(File.Exists(Path.Combine(destDir, "restoreme.txt")));   // arrived at destination
            Assert.False(File.Exists(trashed));                                 // left the trash
        }
        finally { try { Directory.Delete(destDir, true); } catch { } }
    }

    [Fact]
    public async Task Restore_onto_an_existing_name_errors_instead_of_merge_deleting_the_source()
    {
        // bevel-lha4: a same-volume name collision was misread as cross-volume and "recovered" by
        // copy-merge + source delete. It must now be an explicit error, and the trashed source
        // must survive untouched.
        Directory.CreateDirectory(Path.Combine(_trashDir, "folder"));
        File.WriteAllText(Path.Combine(_trashDir, "folder", "inside.txt"), "keep");
        var destDir = Path.Combine(Path.GetTempPath(), "bevel-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(destDir, "folder"));   // a "folder" already exists at the dest
        try
        {
            var mutator = await _provider.GetMutatorAsync(VfsPath.Root("trash"), default);
            var itemPath = VfsPath.Combine(VfsPath.Root("trash"), "folder");

            await Assert.ThrowsAsync<IOException>(async () =>
                await mutator!.MoveAsync(itemPath, new VfsPath("file", destDir), default));

            Assert.True(File.Exists(Path.Combine(_trashDir, "folder", "inside.txt")),
                "the trashed source must be untouched after a collision");
        }
        finally { try { Directory.Delete(destDir, true); } catch { } }
    }
}
