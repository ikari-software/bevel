using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Behavioural tests for FileOperationService over the real LocalFsProvider and temp
/// directories. Covers copy/move semantics, the folder-move source cleanup (bevel-w15/#4),
/// undo, multi-file result integrity, and cancellation safety (bevel-tbo).
/// </summary>
public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly VfsRoot _vfs = new();

    public FileOperationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"bevel-fops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _vfs.Register(new LocalFsProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private FileOperationService NewService(ConflictResolution onConflict = ConflictResolution.Yes)
        => new(_vfs, new StubConflictHandler(onConflict));

    private VfsPath P(params string[] parts) => new("file", Path.Combine(new[] { _root }.Concat(parts).ToArray()));

    private string Abs(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ── Copy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_file_duplicates_content_and_keeps_source()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");

        using var svc = NewService();
        var result = await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.True(File.Exists(Abs("a.txt")));               // source kept
        Assert.Equal("payload", await File.ReadAllTextAsync(Abs("dst", "a.txt")));
    }

    [Fact]
    public async Task Copy_folder_recreates_the_whole_subtree()
    {
        Directory.CreateDirectory(Abs("src", "inner"));
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("src", "top.txt"), "1");
        await File.WriteAllTextAsync(Abs("src", "inner", "leaf.txt"), "2");

        using var svc = NewService();
        var result = await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("src") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.Equal("1", await File.ReadAllTextAsync(Abs("dst", "src", "top.txt")));
        Assert.Equal("2", await File.ReadAllTextAsync(Abs("dst", "src", "inner", "leaf.txt")));
        Assert.True(Directory.Exists(Abs("src")));            // source kept
    }

    // ── Move ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_file_relocates_and_removes_source()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");

        using var svc = NewService();
        var result = await svc.ExecuteAsync(new MoveRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.False(File.Exists(Abs("a.txt")));              // source removed
        Assert.Equal("payload", await File.ReadAllTextAsync(Abs("dst", "a.txt")));
    }

    [Fact]
    public async Task Move_folder_removes_the_emptied_source_directory()
    {
        // bevel #4: a folder move must not leave the original tree behind.
        Directory.CreateDirectory(Abs("src", "inner"));
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("src", "top.txt"), "1");
        await File.WriteAllTextAsync(Abs("src", "inner", "leaf.txt"), "2");

        using var svc = NewService();
        var result = await svc.ExecuteAsync(new MoveRequest
        {
            Sources = new[] { P("src") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.Equal("1", await File.ReadAllTextAsync(Abs("dst", "src", "top.txt")));
        Assert.Equal("2", await File.ReadAllTextAsync(Abs("dst", "src", "inner", "leaf.txt")));
        Assert.False(Directory.Exists(Abs("src")));           // source tree gone
    }

    [Fact]
    public async Task Move_folder_relocates_hidden_children_and_removes_the_source()
    {
        // A Move must relocate EVERYTHING, including hidden files (review #12). The scan
        // now includes hidden/system entries, so the source empties and is removed rather
        // than being silently left behind with a misleading Success.
        Directory.CreateDirectory(Abs("src"));
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("src", "visible.txt"), "v");
        await File.WriteAllTextAsync(Abs("src", ".secret"), "keep me");

        using var svc = NewService();
        await svc.ExecuteAsync(new MoveRequest
        {
            Sources = new[] { P("src") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal("v", await File.ReadAllTextAsync(Abs("dst", "src", "visible.txt")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Abs("dst", "src", ".secret"))); // hidden moved too
        Assert.False(Directory.Exists(Abs("src")));                   // source fully relocated
    }

    [Fact]
    public async Task Undo_move_does_not_overwrite_a_file_recreated_at_the_origin()
    {
        // review #11: undo restore must not silently clobber a file that was recreated at
        // the original location after the move.
        Directory.CreateDirectory(Abs("a"));
        Directory.CreateDirectory(Abs("b"));
        await File.WriteAllTextAsync(Abs("a", "f.txt"), "original");

        using var svc = NewService();
        var move = await svc.ExecuteAsync(new MoveRequest
        {
            Sources = new[] { P("a", "f.txt") },
            Destination = P("b"),
            Timestamp = DateTimeOffset.UtcNow,
        });
        Assert.False(File.Exists(Abs("a", "f.txt")));

        // A new, important file appears where the original used to be.
        await File.WriteAllTextAsync(Abs("a", "f.txt"), "NEW important");

        var undo = await svc.ExecuteAsync(new UndoRequest
        {
            OperationId = move.OperationId,
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.NotEqual(FileOpStatus.Completed, undo.Status);                        // undo refused
        Assert.Equal("NEW important", await File.ReadAllTextAsync(Abs("a", "f.txt"))); // not clobbered
        Assert.Equal("original", await File.ReadAllTextAsync(Abs("b", "f.txt")));      // moved copy intact
    }

    // ── Multi-file integrity (bevel-w15 observable contract) ────────────────

    [Fact]
    public async Task Copy_of_many_files_records_every_result()
    {
        Directory.CreateDirectory(Abs("many"));
        Directory.CreateDirectory(Abs("out"));
        const int n = 50;
        for (var i = 0; i < n; i++)
            await File.WriteAllTextAsync(Abs("many", $"f{i}.txt"), i.ToString());

        using var svc = NewService();
        var result = await svc.ExecuteAsync(new CopyRequest
        {
            Sources = Enumerable.Range(0, n).Select(i => P("many", $"f{i}.txt")).ToArray(),
            Destination = P("out"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.Equal(n, result.SucceededCount);
        Assert.Equal(n, result.ItemResults.Count);
        for (var i = 0; i < n; i++)
            Assert.Equal(i.ToString(), await File.ReadAllTextAsync(Abs("out", $"f{i}.txt")));
    }

    // ── Undo ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Undo_of_a_move_restores_the_source()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");

        using var svc = NewService();
        var move = await svc.ExecuteAsync(new MoveRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });
        Assert.True(svc.Undo.CanUndo);

        var undo = await svc.ExecuteAsync(new UndoRequest
        {
            OperationId = move.OperationId,
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpKind.Undo, undo.Kind);
        Assert.True(File.Exists(Abs("a.txt")));               // restored
        Assert.False(File.Exists(Abs("dst", "a.txt")));       // undone
    }

    // ── Trash + trash-undo (bevel-4u7) ───────────────────────────────────────

    [Fact]
    public async Task Delete_to_trash_moves_the_file_to_the_trash_and_undo_restores_it()
    {
        // Use a provider with an isolated trash directory so the test never touches the
        // real ~/.Trash.
        var trashDir = Path.Combine(_root, "_trash");
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider(trashDir));

        await File.WriteAllTextAsync(Abs("doomed.txt"), "payload");

        using var svc = new FileOperationService(vfs, new StubConflictHandler());
        var del = await svc.ExecuteAsync(new DeleteRequest
        {
            Paths = new[] { P("doomed.txt") },
            ToTrash = true,
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpStatus.Completed, del.Status);
        Assert.False(File.Exists(Abs("doomed.txt")));                 // gone from source
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(trashDir, "doomed.txt"))); // in trash
        Assert.True(svc.Undo.CanUndo);

        var undo = await svc.ExecuteAsync(new UndoRequest
        {
            OperationId = del.OperationId,
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(FileOpKind.Undo, undo.Kind);
        Assert.Equal("payload", await File.ReadAllTextAsync(Abs("doomed.txt"))); // restored
        Assert.False(File.Exists(Path.Combine(trashDir, "doomed.txt")));         // left the trash
    }

    [Fact]
    public async Task UndoAsync_reverses_the_last_operation()
    {
        // review #1: the UI now calls UndoAsync() (not UndoStack.Pop()). Prove that entry
        // point actually reverts the operation.
        var trashDir = Path.Combine(_root, "_trash");
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider(trashDir));
        await File.WriteAllTextAsync(Abs("gone.txt"), "data");

        using var svc = new FileOperationService(vfs, new StubConflictHandler());
        await svc.ExecuteAsync(new DeleteRequest
        {
            Paths = new[] { P("gone.txt") },
            ToTrash = true,
            Timestamp = DateTimeOffset.UtcNow,
        });
        Assert.False(File.Exists(Abs("gone.txt")));
        Assert.True(svc.Undo.CanUndo);

        var result = await svc.UndoAsync();

        Assert.Equal(FileOpKind.Undo, result.Kind);
        Assert.Equal("data", await File.ReadAllTextAsync(Abs("gone.txt"))); // restored
        Assert.False(svc.Undo.CanUndo);                                     // entry consumed
    }

    [Fact]
    public async Task Permanent_delete_removes_the_file_and_is_not_undoable_as_trash()
    {
        await File.WriteAllTextAsync(Abs("gone.txt"), "x");

        using var svc = NewService();
        await svc.ExecuteAsync(new DeleteRequest
        {
            Paths = new[] { P("gone.txt") },
            ToTrash = false,
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.False(File.Exists(Abs("gone.txt")));
        Assert.False(svc.Undo.CanUndo); // permanent delete records no restore entry
    }

    [Fact]
    public async Task Trashing_two_files_with_the_same_name_keeps_both_in_the_trash()
    {
        var trashDir = Path.Combine(_root, "_trash");
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider(trashDir));

        Directory.CreateDirectory(Abs("d1"));
        Directory.CreateDirectory(Abs("d2"));
        await File.WriteAllTextAsync(Abs("d1", "dup.txt"), "first");
        await File.WriteAllTextAsync(Abs("d2", "dup.txt"), "second");

        using var svc = new FileOperationService(vfs, new StubConflictHandler());
        await svc.ExecuteAsync(new DeleteRequest { Paths = new[] { P("d1", "dup.txt") }, ToTrash = true, Timestamp = DateTimeOffset.UtcNow });
        await svc.ExecuteAsync(new DeleteRequest { Paths = new[] { P("d2", "dup.txt") }, ToTrash = true, Timestamp = DateTimeOffset.UtcNow });

        // Both survive in the trash under collision-free names.
        var trashed = Directory.GetFiles(trashDir).Select(File.ReadAllText).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "first", "second" }, trashed);
    }

    // ── Cancellation safety (bevel-tbo) ──────────────────────────────────────

    [Fact]
    public async Task Cancel_after_completion_is_safe()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "x");

        using var svc = NewService();
        await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        // The linked CTS was disposed when ExecuteAsync returned; Cancel must not throw.
        var ex = Record.Exception(() => { svc.Cancel(); svc.Cancel(); });
        Assert.Null(ex);
    }
}
