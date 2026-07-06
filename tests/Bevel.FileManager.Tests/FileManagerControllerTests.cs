using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests the command seam (bevel-o2t): navigation history + events, selection, the cut/copy
/// clipboard, and that mutations delegate correctly to FileOperationService over the real
/// LocalFsProvider and temp directories.
/// </summary>
public sealed class FileManagerControllerTests : IDisposable
{
    private readonly string _root;
    private readonly VfsRoot _vfs = new();
    private readonly FileOperationService _svc;
    private readonly FileManagerController _controller;

    public FileManagerControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"bevel-ctl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _vfs.Register(new LocalFsProvider());
        _svc = new FileOperationService(_vfs, new StubConflictHandler());
        _controller = new FileManagerController(_vfs, _svc);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private VfsPath P(params string[] parts) => new("file", Path.Combine(new[] { _root }.Concat(parts).ToArray()));
    private string Abs(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ── Navigation ─────────────────────────────────────────────────────

    [Fact]
    public void NavigateTo_updates_current_and_raises_events()
    {
        var arrived = new List<VfsPath>();
        var navStateChanges = 0;
        _controller.CurrentDirectoryChanged += p => arrived.Add(p);
        _controller.NavigationStateChanged += () => navStateChanges++;

        var a = P("a");
        var b = P("b");
        _controller.NavigateTo(a);
        _controller.NavigateTo(b);

        Assert.Equal(b, _controller.CurrentDirectory);
        Assert.Equal(new[] { a, b }, arrived);
        Assert.Equal(2, navStateChanges);
    }

    [Fact]
    public void Back_forward_and_up_walk_the_history()
    {
        var a = P("a");
        var ab = P("a", "b");
        _controller.NavigateTo(a);
        _controller.NavigateTo(ab);
        Assert.True(_controller.CanGoBack);
        Assert.False(_controller.CanGoForward);

        _controller.GoBack();
        Assert.Equal(a, _controller.CurrentDirectory);
        Assert.True(_controller.CanGoForward);

        _controller.GoForward();
        Assert.Equal(ab, _controller.CurrentDirectory);

        _controller.GoUp(); // parent of a/b is a
        Assert.Equal(a, _controller.CurrentDirectory);
    }

    [Fact]
    public void Refresh_reraises_current_without_changing_history()
    {
        var arrived = new List<VfsPath>();
        _controller.NavigateTo(P("a"));
        _controller.CurrentDirectoryChanged += p => arrived.Add(p);

        _controller.Refresh();

        Assert.Equal(new[] { P("a") }, arrived);
        Assert.False(_controller.CanGoBack); // still a single entry
    }

    // ── Selection ──────────────────────────────────────────────────────

    [Fact]
    public void DeleteSelectionAsync_is_null_when_nothing_is_selected()
        => Assert.Null(_controller.DeleteSelectionAsync(toTrash: true));

    [Fact]
    public async Task DeleteSelectionAsync_deletes_the_current_selection()
    {
        await File.WriteAllTextAsync(Abs("gone.txt"), "x");
        _controller.SetSelection(new[] { P("gone.txt") });

        var task = _controller.DeleteSelectionAsync(toTrash: false);
        Assert.NotNull(task);
        var result = await task!;

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.False(File.Exists(Abs("gone.txt")));
    }

    // ── Clipboard ──────────────────────────────────────────────────────

    [Fact]
    public async Task PasteAsync_is_null_when_the_clipboard_is_empty()
        => Assert.Null(await _controller.PasteAsync());

    [Fact]
    public async Task Copy_then_paste_duplicates_and_keeps_the_clipboard()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");
        _controller.CopyToClipboard(new[] { P("a.txt") });
        _controller.NavigateTo(P("dst"));

        var result = await _controller.PasteAsync();

        Assert.NotNull(result);
        Assert.True(File.Exists(Abs("a.txt")));            // source kept
        Assert.True(File.Exists(Abs("dst", "a.txt")));     // copied in
        Assert.True(_controller.HasClipboard);             // a copy can be pasted again
    }

    [Fact]
    public async Task Cut_then_paste_moves_and_consumes_the_clipboard()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");
        _controller.CutToClipboard(new[] { P("a.txt") });
        Assert.True(_controller.HasClipboard);
        _controller.NavigateTo(P("dst"));

        var result = await _controller.PasteAsync();

        Assert.NotNull(result);
        Assert.False(File.Exists(Abs("a.txt")));           // moved out
        Assert.True(File.Exists(Abs("dst", "a.txt")));
        Assert.False(_controller.HasClipboard);            // a cut is consumed by the paste
    }

    // ── Mutations delegate to the service ──────────────────────────────

    [Fact]
    public async Task RenameAsync_renames_through_the_service()
    {
        await File.WriteAllTextAsync(Abs("old.txt"), "x");

        var result = await _controller.RenameAsync(P("old.txt"), "new.txt");

        Assert.Equal(FileOpStatus.Completed, result.Status);
        Assert.False(File.Exists(Abs("old.txt")));
        Assert.True(File.Exists(Abs("new.txt")));
    }

    [Fact]
    public async Task Mutations_raise_OperationCompleted()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "x");
        FileOpResult? seen = null;
        _controller.OperationCompleted += r => seen = r;

        await _controller.CopyAsync(new[] { P("a.txt") }, P("dst"));

        Assert.NotNull(seen);
        Assert.Equal(FileOpStatus.Completed, seen!.Status);
    }

    // ── New folder ─────────────────────────────────────────────────────

    [Fact]
    public async Task NewFolderAsync_picks_a_free_name_like_Explorer()
    {
        _controller.NavigateTo(P());

        var first = await _controller.NewFolderAsync();
        var second = await _controller.NewFolderAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(Directory.Exists(Abs("New Folder")));
        Assert.True(Directory.Exists(Abs("New Folder (2)")));
    }

    [Fact]
    public async Task NewFolderAsync_returns_null_for_a_read_only_target()
    {
        var vfs = new VfsRoot();
        vfs.Register(new DecoratingVfsProvider(new LocalFsProvider()) { ReadOnly = true });
        using var svc = new FileOperationService(vfs, new StubConflictHandler());
        var controller = new FileManagerController(vfs, svc);
        controller.NavigateTo(P());

        Assert.Null(await controller.NewFolderAsync());
    }
}
