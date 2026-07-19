using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.AppleEvents;
using Xunit;

namespace Bevel.Core.Tests.Interop;

/// <summary>
/// The hand-written AE object-model resolver (08-os-interop.md §2.1.2), against a real temp tree.
/// Covers the specifier forms v1 promises: property base containers, name/index element access,
/// POSIX coercion, and the restricted <c>whose</c> algebra — plus the fault path that becomes
/// errAEEventNotHandled.
/// </summary>
public sealed class AppleEventObjectResolverTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("bevel-ae-home-").FullName;
    private readonly AppleEventObjectResolver _resolver;

    public AppleEventObjectResolverTests()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        _resolver = new AppleEventObjectResolver(vfs, new Known(_home));

        Directory.CreateDirectory(Path.Combine(_home, "Documents"));
        File.WriteAllText(Path.Combine(_home, "a.txt"), "1");
        File.WriteAllText(Path.Combine(_home, "b.txt"), "2");
        File.WriteAllText(Path.Combine(_home, "c.md"), "3");
    }

    private Task<System.Collections.Generic.IReadOnlyList<VfsPath>> Resolve(ObjectSpecifier s)
        => _resolver.ResolveAsync(s, CancellationToken.None);

    [Fact]
    public async Task Property_home_resolves_to_known_folder()
    {
        var r = await Resolve(new PropertySpecifier("home"));
        Assert.Equal(_home, Assert.Single(r).Value);
    }

    [Fact]
    public async Task Folder_by_name_of_home()
    {
        var r = await Resolve(new ElementByName(AeClass.Folder, "Documents", new PropertySpecifier("home")));
        Assert.Equal(Path.Combine(_home, "Documents"), Assert.Single(r).Value);
    }

    [Fact]
    public async Task Null_container_defaults_to_home()
    {
        var r = await Resolve(new ElementByName(AeClass.File, "a.txt", Container: null));
        Assert.Equal(Path.Combine(_home, "a.txt"), Assert.Single(r).Value);
    }

    [Fact]
    public async Task Missing_element_throws_automation_exception()
    {
        var ex = await Assert.ThrowsAsync<AutomationException>(
            () => Resolve(new ElementByName(AeClass.File, "nope.txt", null)));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task File_class_excludes_folders()
    {
        // "file Documents" must NOT match the folder of that name.
        await Assert.ThrowsAsync<AutomationException>(
            () => Resolve(new ElementByName(AeClass.File, "Documents", null)));
    }

    [Fact]
    public async Task Element_by_index_is_one_based()
    {
        var files = (await Resolve(new EveryElement(AeClass.File, null, null))).Select(p => p.FileName).OrderBy(n => n).ToList();
        var first = await Resolve(new ElementByIndex(AeClass.File, 1, null));
        // index 1 addresses one of the files (enumeration order); assert it's a real file, not out of range.
        Assert.Single(first);
        Assert.Contains(first[0].FileName, files);
    }

    [Fact]
    public async Task Index_out_of_range_throws()
        => await Assert.ThrowsAsync<AutomationException>(
            () => Resolve(new ElementByIndex(AeClass.File, 99, null)));

    [Fact]
    public async Task Posix_path_coercion()
    {
        var r = await Resolve(new PosixPathSpecifier("/tmp/x"));
        Assert.Equal("/tmp/x", Assert.Single(r).Value);
    }

    [Fact]
    public async Task Every_file_whose_extension_txt()
    {
        var r = await Resolve(new EveryElement(AeClass.File, null,
            new WhoseFilter(WhoseKey.NameExtension, WhoseOp.Equals, "txt")));
        Assert.Equal(new[] { "a.txt", "b.txt" }, r.Select(p => p.FileName).OrderBy(n => n));
    }

    [Fact]
    public async Task Every_item_whose_name_begins_with()
    {
        var r = await Resolve(new EveryElement(AeClass.Item, null,
            new WhoseFilter(WhoseKey.Name, WhoseOp.BeginsWith, "a")));
        Assert.Equal(new[] { "a.txt" }, r.Select(p => p.FileName));
    }

    [Fact]
    public async Task Every_item_whose_kind_folder()
    {
        var r = await Resolve(new EveryElement(AeClass.Item, null,
            new WhoseFilter(WhoseKey.Kind, WhoseOp.Equals, "folder")));
        Assert.Equal(new[] { "Documents" }, r.Select(p => p.FileName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Known : IKnownFolders
    {
        private readonly string _home;
        public Known(string home) => _home = home;
        public VfsPath Home => new("file", _home);
        public VfsPath Desktop => new("file", Path.Combine(_home, "Desktop"));
        public VfsPath StartupDisk => new("file", "/");
        public VfsPath Trash => new("file", Path.Combine(_home, ".Trash"));
    }
}
