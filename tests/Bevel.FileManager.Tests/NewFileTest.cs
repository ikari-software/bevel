using System;
using System.IO;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-x6pv: the folder-background "New → Text Document" item was dead (NewItem only handled
/// "folder"). NewFileAsync creates a uniquely-named empty text file, like Explorer.</summary>
public class NewFileTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bevel-newfile-" + Guid.NewGuid().ToString("N"));

    public NewFileTest() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task NewFileAsync_creates_unique_text_documents()
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var controller = new FileManagerController(root, new FileOperationService(root, new DefaultConflictHandler()));

        var p1 = await controller.NewFileAsync(new VfsPath("file", _dir));
        var p2 = await controller.NewFileAsync(new VfsPath("file", _dir));

        Assert.True(File.Exists(Path.Combine(_dir, "New Text Document.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "New Text Document (2).txt")));
        Assert.NotNull(p1);
        Assert.NotEqual(p1, p2);
    }
}
