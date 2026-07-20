using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Xunit;

namespace Bevel.Core.Tests.Interop;

/// <summary>
/// Command-model (08-os-interop.md §3.1) verb behaviour against a real temp filesystem via the
/// LocalFsProvider, with a fake shell surface for the window-coupled verbs. These are the M4
/// exit-criterion "core verbs' single-statement forms" at the seam every inbound surface funnels
/// through — no UI, no permissions.
/// </summary>
public sealed class ShellAutomationTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("bevel-auto-work-").FullName;
    private readonly string _trash = Directory.CreateTempSubdirectory("bevel-auto-trash-").FullName;
    private readonly FakeSurface _surface = new();
    private readonly ShellAutomation _auto;

    public ShellAutomationTests()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider(trashDirectory: _trash));
        _auto = new ShellAutomation(vfs, _surface, new FakeKnownFolders(_work));
    }

    private VfsPath Path_(params string[] parts) => new("file", Path.Combine(new[] { _work }.Concat(parts).ToArray()));
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Make_creates_folder_with_requested_name()
    {
        var created = await _auto.MakeAsync(new VfsPath("file", _work), NewItemKind.Folder, "New Folder", Ct);
        Assert.Equal("New Folder", created.FileName);
        Assert.True(Directory.Exists(Path.Combine(_work, "New Folder")));
    }

    [Fact]
    public async Task Make_numbers_a_colliding_default_name()
    {
        var root = new VfsPath("file", _work);
        await _auto.MakeAsync(root, NewItemKind.Folder, null, Ct);
        var second = await _auto.MakeAsync(root, NewItemKind.Folder, null, Ct);

        Assert.Equal("untitled folder", (await First(root)));   // sanity: first got the base name
        Assert.Equal("untitled folder 2", second.FileName);
    }

    [Fact]
    public async Task Make_file_creates_an_empty_file()
    {
        var created = await _auto.MakeAsync(new VfsPath("file", _work), NewItemKind.File, "notes.txt", Ct);
        var full = Path.Combine(_work, "notes.txt");
        Assert.True(File.Exists(full));
        Assert.Equal(0, new FileInfo(full).Length);
        Assert.Equal("notes.txt", created.FileName);
    }

    [Fact]
    public async Task Delete_to_trash_moves_out_of_source()
    {
        File.WriteAllText(Path.Combine(_work, "gone.txt"), "x");
        await _auto.DeleteAsync(new[] { Path_("gone.txt") }, DeleteMode.Trash, Ct);

        Assert.False(File.Exists(Path.Combine(_work, "gone.txt")));
        Assert.Single(Directory.GetFileSystemEntries(_trash));   // landed in the trash dir
    }

    [Fact]
    public async Task Delete_permanent_removes_the_file()
    {
        File.WriteAllText(Path.Combine(_work, "poof.txt"), "x");
        await _auto.DeleteAsync(new[] { Path_("poof.txt") }, DeleteMode.Permanent, Ct);

        Assert.False(File.Exists(Path.Combine(_work, "poof.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_trash));
    }

    [Fact]
    public async Task Duplicate_file_copies_content_under_finder_name()
    {
        File.WriteAllText(Path.Combine(_work, "x.txt"), "hello");
        var dups = await _auto.DuplicateAsync(new[] { Path_("x.txt") }, target: null, Ct);

        var copy = Path.Combine(_work, "x copy.txt");
        Assert.Equal("x copy.txt", Assert.Single(dups).FileName);
        Assert.Equal("hello", File.ReadAllText(copy));
        Assert.True(File.Exists(Path.Combine(_work, "x.txt")));   // original untouched
    }

    [Fact]
    public async Task Duplicate_folder_recurses()
    {
        Directory.CreateDirectory(Path.Combine(_work, "f", "sub"));
        File.WriteAllText(Path.Combine(_work, "f", "a.txt"), "1");
        File.WriteAllText(Path.Combine(_work, "f", "sub", "b.txt"), "2");

        await _auto.DuplicateAsync(new[] { Path_("f") }, target: null, Ct);

        Assert.Equal("1", File.ReadAllText(Path.Combine(_work, "f copy", "a.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(_work, "f copy", "sub", "b.txt")));
    }

    [Fact]
    public async Task Move_relocates_into_a_container()
    {
        Directory.CreateDirectory(Path.Combine(_work, "dest"));
        File.WriteAllText(Path.Combine(_work, "m.txt"), "hello");
        var moved = await _auto.MoveAsync(new[] { Path_("m.txt") }, new VfsPath("file", Path.Combine(_work, "dest")), Ct);

        Assert.False(File.Exists(Path.Combine(_work, "m.txt")));            // gone from source
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_work, "dest", "m.txt")));   // landed in dest
        Assert.Equal("m.txt", Assert.Single(moved).FileName);
    }

    [Fact]
    public async Task Query_application_reports_version_and_known_folders()
    {
        var snap = await _auto.QueryAsync(AutomationQuery.Application, Ct);

        Assert.False(string.IsNullOrEmpty(snap.Version));
        Assert.Equal(new VfsPath("file", _work), snap.Home);
        Assert.NotNull(snap.Trash);
    }

    [Fact]
    public async Task Reveal_delegates_to_surface_and_counts_selection()
    {
        var res = await _auto.RevealAsync(new[] { Path_("a"), Path_("b") }, new RevealOptions(NewWindow: true), Ct);

        Assert.Equal(2, res.SelectedCount);
        Assert.True(_surface.RevealNewWindow);
        Assert.Equal(2, _surface.RevealItemCount);
    }

    private async Task<string> First(VfsPath container)
    {
        // The lone/earliest entry's leaf name — small helper for the numbering sanity check.
        var names = Directory.GetFileSystemEntries(Path.Combine(_work)).Select(Path.GetFileName).OrderBy(n => n).ToList();
        await Task.CompletedTask;
        return names.First()!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_trash, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeKnownFolders : IKnownFolders
    {
        private readonly string _home;
        public FakeKnownFolders(string home) => _home = home;
        public VfsPath Home => new("file", _home);
        public VfsPath Desktop => new("file", Path.Combine(_home, "Desktop"));
        public VfsPath StartupDisk => new("file", "/");
        public VfsPath Trash => new("file", Path.Combine(_home, ".Trash"));
    }

    private sealed class FakeSurface : IShellSurface
    {
        public bool RevealNewWindow;
        public int RevealItemCount;

        public Task<WindowRef> RevealAsync(IReadOnlyList<VfsPath> items, bool newWindow, CancellationToken ct)
        {
            RevealNewWindow = newWindow;
            RevealItemCount = items.Count;
            return Task.FromResult(new WindowRef(1));
        }

        public Task<WindowRef> OpenAsync(VfsPath container, ViewMode? view, CancellationToken ct)
            => Task.FromResult(new WindowRef(2));
        public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct)
            => Task.CompletedTask;
        public Task<IReadOnlyList<WindowRef>> QueryWindowsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WindowRef>>(Array.Empty<WindowRef>());
        public Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VfsPath>>(Array.Empty<VfsPath>());
        public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct)
            => Task.CompletedTask;
    }
}
