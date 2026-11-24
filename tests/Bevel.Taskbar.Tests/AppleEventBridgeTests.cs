using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.AppleEvents;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The AeSpecifier → ObjectSpecifier conversion (M4-C, bevel-376): the pure mapping between the
/// PAL-neutral parse of an Apple Event object specifier and the command model's specifier that
/// AppleEventObjectResolver resolves. Native parsing is validated live in native/ae-probe2; the
/// resolver has its own tests — this covers the seam between them.
/// </summary>
public class AppleEventBridgeTests
{
    [Fact]
    public void Property_maps()
        => Assert.Equal("home", Assert.IsType<PropertySpecifier>(AppleEventBridge.ConvertSpec(new AeProperty("home"))).Property);

    [Fact]
    public void ByName_with_container_maps()
    {
        var e = Assert.IsType<ElementByName>(AppleEventBridge.ConvertSpec(
            new AeByName("folder", "Documents", new AeProperty("home"))));
        Assert.Equal(AeClass.Folder, e.Class);
        Assert.Equal("Documents", e.Name);
        Assert.IsType<PropertySpecifier>(e.Container);
    }

    [Fact]
    public void ByIndex_maps_including_last()
    {
        var e = Assert.IsType<ElementByIndex>(AppleEventBridge.ConvertSpec(new AeByIndex("item", -1, null)));
        Assert.Equal(AeClass.Item, e.Class);
        Assert.Equal(-1, e.Index);
        Assert.Null(e.Container);
    }

    [Fact]
    public void Every_whose_maps()
    {
        var e = Assert.IsType<EveryElement>(AppleEventBridge.ConvertSpec(
            new AeEvery("file", null, new AeWhose("name extension", "equals", "txt"))));
        Assert.Equal(AeClass.File, e.Class);
        Assert.Null(e.Container);
        Assert.Equal(WhoseKey.NameExtension, e.Filter!.Key);
        Assert.Equal(WhoseOp.Equals, e.Filter.Op);
        Assert.Equal("txt", e.Filter.Value);
    }

    [Theory]
    [InlineData("begins", WhoseOp.BeginsWith)]
    [InlineData("ends", WhoseOp.EndsWith)]
    [InlineData("contains", WhoseOp.Contains)]
    [InlineData("equals", WhoseOp.Equals)]
    public void Whose_operators_map(string op, WhoseOp expected)
    {
        var e = (EveryElement)AppleEventBridge.ConvertSpec(new AeEvery("item", null, new AeWhose("name", op, "x")));
        Assert.Equal(expected, e.Filter!.Op);
    }

    // ── bevel-whz: odoc/open routes each target through IShellAutomation.OpenAsync ──

    private sealed class RecordingAutomation : IShellAutomation
    {
        public List<VfsPath> Opens { get; } = new();
        public List<string> Launches { get; } = new();

        public Task LaunchAsync(string appId, CancellationToken ct)
        {
            Launches.Add(appId);
            return Task.CompletedTask;
        }

        public Task<WindowRef> OpenAsync(VfsPath containerOrFile, OpenOptions opts, CancellationToken ct)
        {
            Opens.Add(containerOrFile);
            return Task.FromResult(new WindowRef(1));
        }

        public Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct) => throw new NotSupportedException();
        public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct) => throw new NotSupportedException();
        public Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VfsPath>> MoveAsync(IReadOnlyList<VfsPath> items, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();
        public Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task QuitAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // ── bevel-odf: async AE answers (suspended reply) — uncapped count, window/selection via automation ──

    private sealed class Known(string home) : IKnownFolders
    {
        public VfsPath Home => new("file", home);
        public VfsPath Desktop => new("file", home + "/Desktop");
        public VfsPath StartupDisk => new("file", "/");
        public VfsPath Trash => new("file", home + "/.Trash");
    }

    private sealed class QueryAutomation : IShellAutomation
    {
        public BevelStateSnapshot Snapshot { get; set; } = new();
        public CancellationToken LastQueryCt { get; private set; }
        public Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct)
        {
            LastQueryCt = ct;
            return ct.IsCancellationRequested
                ? Task.FromCanceled<BevelStateSnapshot>(ct)
                : Task.FromResult(Snapshot);
        }
        public Task<WindowRef> OpenAsync(VfsPath containerOrFile, OpenOptions opts, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct) => throw new NotSupportedException();
        public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct) => throw new NotSupportedException();
        public Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VfsPath>> MoveAsync(IReadOnlyList<VfsPath> items, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();
        public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task LaunchAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
        public Task QuitAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Open_routes_each_target_through_OpenAsync()
    {
        var automation = new RecordingAutomation();
        // Paths only: the resolver/known-folders are not touched on the Open path.
        var request = new AppleEventInbound.AeRequest(AppleEventInbound.Verb.Open,
            new[] { "/tmp", "/Users" }, Array.Empty<AeSpecifier>(), null, null);

        await AppleEventBridge.DispatchAsync(automation, null!, null!, request);

        Assert.Equal(new[] { new VfsPath("file", "/tmp"), new VfsPath("file", "/Users") }, automation.Opens);
    }

    // ── bevel-vs10: AE 'launch program' routes to LaunchAsync, bundle id only ──

    [Fact]
    public async Task Launch_passes_the_bundle_id_to_LaunchAsync()
    {
        var automation = new RecordingAutomation();
        var request = new AppleEventInbound.AeRequest(AppleEventInbound.Verb.Launch,
            Array.Empty<string>(), Array.Empty<AeSpecifier>(), null, "com.apple.TextEdit");

        await AppleEventBridge.DispatchAsync(automation, null!, null!, request);

        Assert.Equal(new[] { "com.apple.TextEdit" }, automation.Launches);
    }

    [Fact]
    public async Task Launch_refuses_a_filesystem_path()
    {
        var automation = new RecordingAutomation();
        var request = new AppleEventInbound.AeRequest(AppleEventInbound.Verb.Launch,
            Array.Empty<string>(), Array.Empty<AeSpecifier>(), null, "/Applications/TextEdit.app");

        await AppleEventBridge.DispatchAsync(automation, null!, null!, request);

        Assert.Empty(automation.Launches);   // same LooksLikePath guard as bevelctl/bevel://
    }

    private static (AppleEventObjectResolver Resolver, Known Known, string Home) MakeResolver()
    {
        var home = Directory.CreateTempSubdirectory("bevel-ae-odf-").FullName.Replace('\\', '/');
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var known = new Known(home);
        return (new AppleEventObjectResolver(vfs, known), known, home);
    }

    [Fact]
    public async Task AnswerAsync_count_is_not_capped()
    {
        var (resolver, known, home) = MakeResolver();
        var big = Path.Combine(home, "big");
        Directory.CreateDirectory(big);
        for (var i = 0; i < 5000; i++) File.WriteAllText(Path.Combine(big, $"f{i}.txt"), "x");
        // 5000 > UiThreadEnumerationCap (4096): the async path must walk unbounded.
        var query = new AeQuery(AeQueryKind.ResolvePaths,
            new AeEvery("item", new AeByName("folder", "big", new AeProperty("home")), null), QueryOp.Count);

        var result = await AppleEventBridge.AnswerAsync(query, known, new QueryAutomation(), resolver);

        Assert.Equal(new AeCount(5000), result);
    }

    [Fact]
    public async Task AnswerAsync_window_count_comes_from_the_automation_query()
    {
        var (resolver, known, _) = MakeResolver();
        var automation = new QueryAutomation
        {
            Snapshot = new BevelStateSnapshot { Windows = new[] { new WindowRef(1), new WindowRef(2) } }
        };
        var query = new AeQuery(AeQueryKind.WindowCount, null, QueryOp.Count);

        var result = await AppleEventBridge.AnswerAsync(query, known, automation, resolver);

        Assert.Equal(new AeCount(2), result);
    }

    [Fact]
    public async Task AnswerAsync_missing_folder_counts_zero()
    {
        var (resolver, known, _) = MakeResolver();
        var query = new AeQuery(AeQueryKind.ResolvePaths,
            new AeByName("folder", "no-such-folder", new AeProperty("home")), QueryOp.Count);

        var result = await AppleEventBridge.AnswerAsync(query, known, new QueryAutomation(), resolver);

        Assert.Equal(new AeCount(0), result);
    }

    // ce-code-review bevel-odf: the PAL's 10 s suspended-query timeout is a CancellationToken wired
    // into AnswerAsync — these pin the bridge side of that contract (the PAL CTS itself needs a live
    // macOS AE target to exercise end-to-end).

    [Fact]
    public async Task AnswerAsync_forwards_the_timeout_token_to_the_automation_query()
    {
        var (resolver, known, _) = MakeResolver();
        var automation = new QueryAutomation();
        var query = new AeQuery(AeQueryKind.WindowCount, null, QueryOp.Count);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AppleEventBridge.AnswerAsync(query, known, automation, resolver, cts.Token));
        Assert.True(automation.LastQueryCt.IsCancellationRequested);
    }

    [Fact]
    public async Task AnswerAsync_forwards_the_timeout_token_into_the_resolver_walk()
    {
        var (resolver, known, home) = MakeResolver();
        var big = Path.Combine(home, "big");
        Directory.CreateDirectory(big);
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(big, $"f{i}.txt"), "x");
        var query = new AeQuery(AeQueryKind.ResolvePaths,
            new AeEvery("item", new AeByName("folder", "big", new AeProperty("home")), null), QueryOp.Count);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A cancelled token must abort the directory walk, not just the await.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AppleEventBridge.AnswerAsync(query, known, new QueryAutomation(), resolver, cts.Token));
    }
}
