using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// Covers trashing directories (bevel-bb0). The original bug: DeleteAsync moved a folder to the
/// trash with Directory.Move, which throws when the folder and the trash live on different volumes
/// (cross-device rename / EXDEV). The fix falls back to a recursive copy + delete in that case.
/// A second volume can't be forced on a single-volume CI box, so the copy path is exercised
/// directly via <see cref="LocalTrash.CopyDirectory"/>; the same-volume rename path is covered
/// end-to-end through the public mutator API.
/// </summary>
public class LocalTrashTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _trashDir;

    public LocalTrashTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"bevel-trash-test-{Guid.NewGuid():N}");
        _trashDir = Path.Combine(_testDir, "trash");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public async Task DeleteAsync_trashes_a_folder_with_nested_contents()
    {
        // A folder with a file and a nested subfolder — the shape that broke when Directory.Move
        // was the only strategy.
        var folder = Path.Combine(_testDir, "myfolder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");

        var provider = new LocalFsProvider(_trashDir);
        var parent = new VfsPath("file", _testDir);
        var mutator = await provider.GetMutatorAsync(parent, CancellationToken.None);
        Assert.NotNull(mutator);

        var trashed = await mutator!.DeleteAsync(
            VfsPath.Combine(parent, "myfolder"), toTrash: true, CancellationToken.None);

        // Source is gone, the whole tree landed in the trash, and the returned path points at it.
        Assert.False(Directory.Exists(folder));
        Assert.NotNull(trashed);
        var landed = trashed!.Value.Value;
        Assert.True(Directory.Exists(landed));
        Assert.Equal("a", File.ReadAllText(Path.Combine(landed, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(landed, "sub", "b.txt")));
    }

    [Fact]
    public void CopyDirectory_recreates_the_full_tree_at_the_destination()
    {
        // This is the cross-volume fallback's substance: recursively copy every file and
        // subdirectory. Verified same-volume here since the recursion is volume-agnostic.
        var source = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        File.WriteAllText(Path.Combine(source, "top.txt"), "top");
        File.WriteAllText(Path.Combine(source, "nested", "mid.txt"), "mid");
        File.WriteAllText(Path.Combine(source, "nested", "deeper", "leaf.txt"), "leaf");

        var dest = Path.Combine(_testDir, "dst");
        LocalTrash.CopyDirectory(source, dest);

        Assert.Equal("top", File.ReadAllText(Path.Combine(dest, "top.txt")));
        Assert.Equal("mid", File.ReadAllText(Path.Combine(dest, "nested", "mid.txt")));
        Assert.Equal("leaf", File.ReadAllText(Path.Combine(dest, "nested", "deeper", "leaf.txt")));
        // Source is left untouched — copy, not move.
        Assert.True(File.Exists(Path.Combine(source, "top.txt")));
    }

    [Fact]
    public void MoveToTrash_moves_a_single_file()
    {
        var file = Path.Combine(_testDir, "solo.txt");
        File.WriteAllText(file, "solo");
        var dest = Path.Combine(_testDir, "solo-trashed.txt");

        LocalTrash.MoveToTrash(file, dest);

        Assert.False(File.Exists(file));
        Assert.Equal("solo", File.ReadAllText(dest));
    }

    [Fact]
    public void MoveToTrash_onto_an_existing_directory_throws_and_preserves_the_source()
    {
        // The collision that MoveAsync's reuse exposed: a same-volume Directory.Move onto an existing
        // folder throws IOException, which the EXDEV fallback would misread as cross-volume and
        // "recover" by copy-merge + delete of the source. Guard it: collision must error, source intact.
        var source = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var dest = Path.Combine(_testDir, "dst");
        Directory.CreateDirectory(dest);

        Assert.Throws<IOException>(() => LocalTrash.MoveToTrash(source, dest));

        // Source is untouched — no merge-then-delete data loss.
        Assert.True(Directory.Exists(source));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "keep.txt")));
    }

    [Fact]
    public void MoveToTrash_onto_an_existing_file_throws_and_preserves_the_source()
    {
        var source = Path.Combine(_testDir, "src.txt");
        File.WriteAllText(source, "src");
        var dest = Path.Combine(_testDir, "dst.txt");
        File.WriteAllText(dest, "dst");

        Assert.Throws<IOException>(() => LocalTrash.MoveToTrash(source, dest));

        Assert.Equal("src", File.ReadAllText(source));   // source intact
        Assert.Equal("dst", File.ReadAllText(dest));     // destination not clobbered
    }

    [Fact]
    public async Task MoveAsync_onto_a_colliding_name_throws_without_losing_the_source()
    {
        // End-to-end through the mutator: moving a folder into a parent that already holds a folder of
        // the same name must fail cleanly, not silently merge and delete the moved folder (REL-4).
        var srcParent = Path.Combine(_testDir, "from");
        var destParent = Path.Combine(_testDir, "to");
        Directory.CreateDirectory(destParent);
        Directory.CreateDirectory(Path.Combine(srcParent, "dup"));
        File.WriteAllText(Path.Combine(srcParent, "dup", "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(destParent, "dup"));   // pre-existing collision at destination

        var provider = new LocalFsProvider(_trashDir);
        var from = new VfsPath("file", srcParent);
        var mutator = await provider.GetMutatorAsync(from, CancellationToken.None);
        Assert.NotNull(mutator);

        await Assert.ThrowsAsync<IOException>(() =>
            mutator!.MoveAsync(VfsPath.Combine(from, "dup"), new VfsPath("file", destParent), CancellationToken.None).AsTask());

        // The moved folder survives at its origin.
        Assert.True(Directory.Exists(Path.Combine(srcParent, "dup")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(srcParent, "dup", "a.txt")));
    }
}
