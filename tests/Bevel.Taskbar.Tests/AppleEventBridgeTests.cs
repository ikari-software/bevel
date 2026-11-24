using System;
using System.Collections.Generic;
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
}
