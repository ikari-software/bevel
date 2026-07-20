using Bevel.App;
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
}
