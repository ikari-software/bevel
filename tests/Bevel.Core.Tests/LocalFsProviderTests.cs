using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

public class LocalFsProviderTests : IDisposable
{
    private readonly LocalFsProvider _provider = new();
    private readonly string _testDir;

    public LocalFsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"bevel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void Scheme_is_file()
    {
        Assert.Equal("file", _provider.Scheme);
    }

    [Fact]
    public async Task ResolveAsync_returns_folder_node_for_directory()
    {
        var path = new VfsPath("file", _testDir);
        var node = await _provider.ResolveAsync(path, CancellationToken.None);

        Assert.Equal(VfsNodeKind.Folder, node.Kind);
        Assert.True(node.MightHaveChildren);
        Assert.Null(node.Size); // directories have no size
    }

    [Fact]
    public async Task ResolveAsync_returns_file_node_for_file()
    {
        var file = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(file, "hello");
        var path = new VfsPath("file", file);

        var node = await _provider.ResolveAsync(path, CancellationToken.None);

        Assert.Equal(VfsNodeKind.File, node.Kind);
        Assert.Equal(5, node.Size);
        Assert.Equal("test.txt", node.DisplayName);
    }

    [Fact]
    public async Task EnumerateAsync_returns_mixed_files_and_dirs()
    {
        File.WriteAllText(Path.Combine(_testDir, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        File.WriteAllText(Path.Combine(_testDir, "subdir", "b.txt"), "b");

        var path = new VfsPath("file", _testDir);
        var items = new List<IVfsNode>();

        await foreach (var node in _provider.EnumerateAsync(path, new EnumerateOptions(), CancellationToken.None))
        {
            items.Add(node);
        }

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.DisplayName == "a.txt" && i.Kind == VfsNodeKind.File);
        Assert.Contains(items, i => i.DisplayName == "subdir" && i.Kind == VfsNodeKind.Folder);
    }

    [Fact]
    public async Task EnumerateAsync_does_not_throw_on_directories()
    {
        // This is the bug we hit: FileInfo throws on directory paths.
        Directory.CreateDirectory(Path.Combine(_testDir, "dir1"));
        Directory.CreateDirectory(Path.Combine(_testDir, "dir2"));

        var path = new VfsPath("file", _testDir);
        var items = new List<IVfsNode>();

        // Should not throw
        await foreach (var node in _provider.EnumerateAsync(path, new EnumerateOptions(), CancellationToken.None))
        {
            items.Add(node);
        }

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(VfsNodeKind.Folder, i.Kind));
    }

    [Fact]
    public async Task EnumerateAsync_size_is_null_for_directories()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "mydir"));

        var path = new VfsPath("file", _testDir);
        await foreach (var node in _provider.EnumerateAsync(path, new EnumerateOptions(), CancellationToken.None))
        {
            Assert.Null(node.Size);
        }
    }

    [Fact]
    public async Task EnumerateAsync_size_is_correct_for_files()
    {
        File.WriteAllText(Path.Combine(_testDir, "big.txt"), new string('x', 1000));

        var path = new VfsPath("file", _testDir);
        await foreach (var node in _provider.EnumerateAsync(path, new EnumerateOptions(), CancellationToken.None))
        {
            if (node.DisplayName == "big.txt")
                Assert.Equal(1000, node.Size);
        }
    }

    [Fact]
    public async Task EnumerateAsync_respects_limit()
    {
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_testDir, $"file{i}.txt"), "x");

        var path = new VfsPath("file", _testDir);
        var count = 0;

        await foreach (var _ in _provider.EnumerateAsync(path, new EnumerateOptions { Limit = 3 }, CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task OpenReadAsync_returns_readable_stream()
    {
        File.WriteAllText(Path.Combine(_testDir, "data.bin"), "test data");

        var path = new VfsPath("file", Path.Combine(_testDir, "data.bin"));
        using var stream = await _provider.OpenReadAsync(path, CancellationToken.None);

        Assert.True(stream.CanRead);
        using var reader = new StreamReader(stream);
        Assert.Equal("test data", await reader.ReadToEndAsync());
    }

    [Fact]
    public void ValidateName_rejects_empty_name()
    {
        var path = new VfsPath("file", _testDir);
        var result = _provider.ValidateName(path, "");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateName_rejects_existing_name()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.txt"), "");
        var path = new VfsPath("file", _testDir);
        var result = _provider.ValidateName(path, "existing.txt");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateName_accepts_valid_name()
    {
        var path = new VfsPath("file", _testDir);
        var result = _provider.ValidateName(path, "newfile.txt");
        Assert.True(result.IsValid);
    }
}
