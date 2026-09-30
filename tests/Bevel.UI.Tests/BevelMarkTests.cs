using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.UI;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.UI.Tests;

/// <summary>The Bevel mark's decision tables: which layers appear at which size, the nine spectrum edges,
/// and the layer order that makes the core read as INSIDE the glass.</summary>
public class BevelMarkTests
{
    [Theory]
    [InlineData(16, false)]
    [InlineData(20, false)]
    [InlineData(23.9, false)]
    [InlineData(24, true)]
    [InlineData(96, true)]
    public void The_core_appears_only_from_the_full_detail_size(double size, bool expectCore)
    {
        var layers = BevelMark.LayersFor(size);
        Assert.Equal(expectCore, layers.HasFlag(BevelMark.Layers.Core));
        Assert.Equal(expectCore, layers.HasFlag(BevelMark.Layers.Rays));
    }

    [Fact]
    public void The_detail_option_can_only_add_the_core_never_remove_it()
    {
        Assert.True(BevelMark.LayersFor(20, fullDetail: true).HasFlag(BevelMark.Layers.Core));
        Assert.True(BevelMark.LayersFor(96, fullDetail: false).HasFlag(BevelMark.Layers.Core));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(96)]
    public void The_glass_and_the_edges_are_present_at_every_size(double size)
    {
        var layers = BevelMark.LayersFor(size);
        Assert.True(layers.HasFlag(BevelMark.Layers.FarFaces));
        Assert.True(layers.HasFlag(BevelMark.Layers.NearFaces));
        Assert.True(layers.HasFlag(BevelMark.Layers.Edges));
    }

    [Fact]
    public void All_nine_visible_edges_are_coloured_and_no_two_share_a_colour()
    {
        Assert.Equal(9, BevelMark.Edges.Count);   // 6 silhouette + 3 spokes: "all edges have some colour"
        Assert.Equal(9, BevelMark.Edges.Select(e => e.Hex.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(9, BevelMark.Edges.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public void The_spectrum_is_not_a_four_colour_grid()
    {
        // A 2x2 blue/green/red/yellow palette is another company's logo. The edges must be a spread of
        // hues: no more than two edges may fall in any one 45-degree hue sector.
        var sectors = BevelMark.Edges
            .Select(e => Color.Parse(e.Hex))
            .Select(c => (int)(c.ToHsv().H / 45))
            .GroupBy(s => s);
        Assert.All(sectors, g => Assert.InRange(g.Count(), 1, 3));
        Assert.True(sectors.Count() >= 6, "the spectrum should span most of the hue wheel");
    }

    [AvaloniaTheory]
    [InlineData(20, false, 15)]   // 3 far + 3 near + 9 edges
    [InlineData(20, true, 23)]    // + rays, 3 core faces, 4 core edges
    [InlineData(96, false, 23)]
    public void Create_draws_the_expected_layers(double size, bool fullDetail, int expectedPaths)
    {
        var mark = Assert.IsType<Viewbox>(BevelMark.Create(size, fullDetail));
        Assert.Equal(size, mark.Width);
        Assert.Equal(size, mark.Height);
        var canvas = Assert.IsType<Canvas>(mark.Child);
        Assert.Equal(expectedPaths, canvas.Children.OfType<Path>().Count());
    }

    [Fact]
    public void Draw_order_puts_the_core_under_the_near_glass_and_the_edges_on_top()
    {
        var order = BevelMark.DrawOrder.ToList();
        int far = order.IndexOf(BevelMark.Layers.FarFaces), core = order.IndexOf(BevelMark.Layers.Core),
            near = order.IndexOf(BevelMark.Layers.NearFaces), edges = order.IndexOf(BevelMark.Layers.Edges);
        Assert.True(far < core, "far faces are the bottom layer");
        Assert.True(core < near, "the near glass is painted OVER the core, or the core looks stuck on the front");
        Assert.True(near < edges, "the crisp coloured edges are on top");
        // every drawable layer is scheduled exactly once
        Assert.Equal(5, order.Distinct().Count());
    }

    [AvaloniaFact]
    public void Create_paints_every_core_face_before_every_near_glass_face()
    {
        var canvas = Assert.IsType<Canvas>(Assert.IsType<Viewbox>(BevelMark.Create(96)).Child);
        var paths = canvas.Children.OfType<Path>().ToList();
        static int[] Find(System.Collections.Generic.List<Path> ps, params (byte R, byte G, byte B)[] rgbs) =>
            rgbs.Select(c => ps.FindIndex(p => p.Fill is ISolidColorBrush b && b.Color.R == c.R && b.Color.G == c.G && b.Color.B == c.B)).ToArray();

        var core = Find(paths, (0xF2, 0xF7, 0xFF), (0x6E, 0x9B, 0xE6), (0x2B, 0x5D, 0xB4));   // solid core faces
        var near = Find(paths, (0xEA, 0xF2, 0xFF), (0x7B, 0xA3, 0xE6), (0x3A, 0x66, 0xBC));   // translucent near faces
        Assert.All(core, i => Assert.True(i >= 0, "core face present at 96px"));
        Assert.All(near, i => Assert.True(i >= 0, "near face present"));
        Assert.True(core.Max() < near.Min(), "the glass must be painted over the core");

        // ...and the near faces really are translucent (otherwise they would hide the core).
        Assert.All(near, i => Assert.InRange(((ISolidColorBrush)paths[i].Fill!).Color.A, 1, 254));
        var firstEdge = paths.FindIndex(p => p.Stroke is not null && p.StrokeThickness >= 5);
        Assert.True(firstEdge > near.Max(), "coloured shell edges are on top of the glass");
    }

    [AvaloniaFact]
    public void Edges_use_round_caps_so_small_sizes_do_not_show_square_nubs()
    {
        var canvas = Assert.IsType<Canvas>(Assert.IsType<Viewbox>(BevelMark.Create(20)).Child);
        Assert.All(canvas.Children.OfType<Path>().Where(p => p.Stroke is not null),
            p => Assert.Equal(PenLineCap.Round, p.StrokeLineCap));
    }
}
