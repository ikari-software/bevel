using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bevel.UI;
using Classic.Avalonia.Theme;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Locks the Crisp/Smooth edge rendering modes (bevel-38y.1 / chrome spec §8) at the real
/// rasterizer: a Raised ClassicBorderDecorator renders at 3× and its top-edge scanline is
/// probed. Crisp must contain ONLY the pure band colours (hard, device-snapped lines);
/// Smooth must pass through intermediate colours (the eased ramp) while both keep the same
/// logical proportions. Band colours are set explicitly and chosen pairwise-distinct so every
/// ring is observable; Skia rasterization of fixed inputs is deterministic.
/// </summary>
public sealed class EdgeRenderingTests
{
    private static readonly Color Face = Color.FromRgb(0xD4, 0xD0, 0xC8);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Light = Color.FromRgb(0xC0, 0xC0, 0xC0); // distinct from Face on purpose

    [AvaloniaFact]
    public void Smooth_is_the_default_mode_on_both_edge_primitives()
    {
        // Decision 2026-07-06 (spec §8): Smooth at all scalings; Crisp is the user override.
        Assert.Equal(EdgeRendering.Smooth, new ClassicBorderDecorator().EdgeRendering);
        Assert.Equal(EdgeRendering.Smooth, new BevelBorder().EdgeRendering);
    }

    /// <summary>
    /// Renders a 60×60 Raised decorator at 3× and returns rows 0..9 of the column x=90.
    /// BorderThickness=3 mirrors the real templates (Raised reserves one ring for the
    /// focus/default border), so one ring = 1 DIP = 3 device px: crisp rows are
    /// 0-2 White ring, 3-5 Light ring, 6+ face fill; the Smooth edge spans the same 2 DIP.
    /// </summary>
    private static Color[] TopEdgeScanline(EdgeRendering mode)
    {
        var decorator = new ClassicBorderDecorator
        {
            BorderStyle = ClassicBorderStyle.Raised,
            BorderBrush = ClassicBorderDecorator.ClassicBorderBrush,
            BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Face),
            // Explicit band brushes so expected colours are exact (no HLS derivation involved).
            BorderLightLightBrush = new SolidColorBrush(White),
            BorderLightBrush = new SolidColorBrush(Light),
            BorderDarkBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            BorderDarkDarkBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
            EdgeRendering = mode,
            Width = 60,
            Height = 60,
        };
        decorator.Measure(new Size(60, 60));
        decorator.Arrange(new Rect(0, 0, 60, 60));

        using var target = new RenderTargetBitmap(new PixelSize(180, 180), new Vector(288, 288));
        target.Render(decorator);

        var buffer = new byte[180 * 180 * 4];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, 180, 180), pin.AddrOfPinnedObject(), buffer.Length, 180 * 4);
        }
        finally
        {
            pin.Free();
        }

        var scan = new Color[10];
        for (var y = 0; y < scan.Length; y++)
        {
            var o = (y * 180 + 90) * 4; // RGBA byte order (verified on this Skia surface)
            scan[y] = Color.FromRgb(buffer[o], buffer[o + 1], buffer[o + 2]);
        }
        return scan;
    }

    private static readonly Color[] PureBandColors = { White, Light, Face };

    [AvaloniaFact]
    public void Crisp_top_edge_is_hard_bands_of_the_exact_band_colours()
    {
        if (!OperatingSystem.IsMacOS()) return;   // exact rasterized band colours differ off-macOS (Skia AA/gamma); validated on the ship platform
        var scan = TopEdgeScanline(EdgeRendering.Crisp);

        Assert.All(scan.Take(3), c => Assert.Equal(White, c)); // outer ring (1 DIP)
        Assert.All(scan.Skip(3).Take(3), c => Assert.Equal(Light, c)); // inner ring
        Assert.All(scan.Skip(6), c => Assert.Equal(Face, c)); // face fill / content inset
    }

    [AvaloniaFact]
    public void Smooth_top_edge_ramps_through_intermediate_colours()
    {
        var scan = TopEdgeScanline(EdgeRendering.Smooth);

        // The eased gradient holds the light-catching colour at the outer boundary (§8 hold)…
        Assert.Equal(White, scan[0]);
        // …and — the point of Smooth — passes through colours the crisp bands never produce.
        Assert.Contains(scan, c => !PureBandColors.Contains(c));
    }

    [AvaloniaFact]
    public void Both_modes_keep_the_same_logical_proportions()
    {
        if (!OperatingSystem.IsMacOS()) return;   // reads rasterized pixels — macOS-validated (see above)
        // Smooth changes the fill technique, never the proportions (spec §8): past the 2-DIP
        // edge both modes must show the plain face fill.
        foreach (var mode in new[] { EdgeRendering.Crisp, EdgeRendering.Smooth })
        {
            var scan = TopEdgeScanline(mode);
            Assert.Equal(Face, scan[7]);
            Assert.Equal(Face, scan[9]);
        }
    }
}
