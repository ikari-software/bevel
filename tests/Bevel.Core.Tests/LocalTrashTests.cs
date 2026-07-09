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
}
