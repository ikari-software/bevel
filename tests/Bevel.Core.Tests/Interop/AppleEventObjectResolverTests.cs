using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    // Canonical '/' form (what VfsPath.Value always is); '/' is a valid separator for .NET file I/O on Windows.
    private readonly string _home = Directory.CreateTempSubdirectory("bevel-ae-home-").FullName.Replace('\\', '/');
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
        Assert.Equal(Path.Combine(_home, "Documents").Replace('\\', '/'), Assert.Single(r).Value);
    }

    [Fact]
    public async Task Null_container_defaults_to_home()
    {
        var r = await Resolve(new ElementByName(AeClass.File, "a.txt", Container: null));
        Assert.Equal(Path.Combine(_home, "a.txt").Replace('\\', '/'), Assert.Single(r).Value);
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

    // ── bevel-o3sa: the UI-thread get/count/exists walk must be bounded / short-circuit ──────────
    //
    // These use a synthetic provider over an enormous (lazy) directory and assert how many raw entries
    // the resolver actually pulled, which is the thing that would freeze the UI thread if unbounded.

    private static AppleEventObjectResolver ResolverOver(CountingProvider provider)
    {
        var vfs = new VfsRoot();
        vfs.Register(provider);   // registers under scheme "file", so home routes here
        return new AppleEventObjectResolver(vfs, new Known("/home"));
    }

    [Fact]
    public async Task Exists_short_circuits_on_first_match_without_walking_the_directory()
    {
        var provider = new CountingProvider(total: 1_000_000, matchAll: true);
        var resolver = ResolverOver(provider);

        // `exists every file of home` over a million-entry directory: the first entry already matches,
        // so it must return after pulling a HANDFUL of entries — never the whole (or even capped) walk.
        var exists = await resolver.ExistsAsync(
            new EveryElement(AeClass.File, null, null), CancellationToken.None,
            AppleEventObjectResolver.UiThreadEnumerationCap);

        Assert.True(exists);
        Assert.True(provider.Yielded <= 4, $"exists walked {provider.Yielded} entries; expected it to stop at the first match.");
    }

    [Fact]
    public async Task Exists_with_no_match_is_bounded_by_the_ui_cap()
    {
        var provider = new CountingProvider(total: 1_000_000, matchAll: false);   // nothing matches the filter
        var resolver = ResolverOver(provider);

        // Worst case: a huge directory with ZERO matches. Without the cap this walks all 1,000,000
        // entries on the UI thread; with it, the walk stops at the cap.
        var exists = await resolver.ExistsAsync(
            new EveryElement(AeClass.File, null, new WhoseFilter(WhoseKey.NameExtension, WhoseOp.Equals, "nomatch")),
            CancellationToken.None, AppleEventObjectResolver.UiThreadEnumerationCap);

        Assert.False(exists);
        Assert.Equal(AppleEventObjectResolver.UiThreadEnumerationCap, provider.Yielded);
    }

    [Fact]
    public async Task Count_via_get_is_capped_on_the_ui_thread()
    {
        var provider = new CountingProvider(total: 1_000_000, matchAll: true);
        var resolver = ResolverOver(provider);

        var result = await resolver.ResolveAsync(
            new EveryElement(AeClass.File, null, null), CancellationToken.None,
            AppleEventObjectResolver.UiThreadEnumerationCap);

        // `count of every file of home` returns at most the cap, and never walked past it.
        Assert.Equal(AppleEventObjectResolver.UiThreadEnumerationCap, result.Count);
        Assert.Equal(AppleEventObjectResolver.UiThreadEnumerationCap, provider.Yielded);
    }

    [Fact]
    public async Task Uncapped_resolve_still_walks_everything()
    {
        // The async command path (cap = 0) keeps its full semantics: it is NOT on the UI thread.
        var provider = new CountingProvider(total: 2_500, matchAll: true);
        var resolver = ResolverOver(provider);

        var result = await resolver.ResolveAsync(new EveryElement(AeClass.File, null, null));

        Assert.Equal(2_500, result.Count);
        Assert.Equal(2_500, provider.Yielded);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A provider over a lazily-generated directory of <c>total</c> files. Honours
    /// <see cref="EnumerateOptions.Limit"/> exactly like <see cref="LocalFsProvider"/> and counts how
    /// many entries it actually yields, so a test can assert the resolver never over-walks.</summary>
    private sealed class CountingProvider : IVfsProvider
    {
        private readonly int _total;
        private readonly bool _matchAll;   // when false, names end ".other" so a ".nomatch" filter never hits
        public int Yielded;

        public CountingProvider(int total, bool matchAll) { _total = total; _matchAll = matchAll; }

        public string Scheme => "file";

        public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
            VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            var remaining = options.Limit;
            for (var i = 0; i < _total; i++)
            {
                ct.ThrowIfCancellationRequested();
                Yielded++;
                await Task.Yield();
                yield return new SyntheticNode(VfsPath.Combine(folder, $"f{i}.{(_matchAll ? "txt" : "other")}"));
                if (remaining > 0 && --remaining <= 0) yield break;
            }
        }

        public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct) => throw new NotSupportedException();
        public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;
        public NameValidationResult ValidateName(VfsPath folder, string proposedName) => NameValidationResult.Ok;
    }

    private sealed class SyntheticNode : IVfsNode
    {
        public SyntheticNode(VfsPath path) => Path = path;
        public VfsPath Path { get; }
        public string DisplayName => Path.FileName;
        public VfsNodeKind Kind => VfsNodeKind.File;
        public bool MightHaveChildren => false;
        public long? Size => null;
        public DateTimeOffset? Modified => null;
        public string TypeDescription => "File";
        public IconKey IconKey => IconKey.File(".txt");
        public VfsCapabilities Caps => VfsCapabilities.CopySource;
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
