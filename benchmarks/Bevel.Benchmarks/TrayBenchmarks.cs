using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Taskbar;
using BenchmarkDotNet.Attributes;

namespace Bevel.Benchmarks;

/// <summary>
/// <see cref="TrayIconTint.Process"/> — decode a captured status-item PNG, scan every pixel to decide
/// whether it is a sparse monochrome "template" glyph, and (when it is) recolour it to the bar ink.
/// Run once per mirrored tray icon on every capture tick. Benchmarked at the two real icon sizes
/// (16px, 32px) with a template glyph so BOTH the analysis scan and the recolour pass execute.
/// </summary>
[MemoryDiagnoser]
public class TrayTintBenchmarks
{
    private byte[] _png16 = null!;
    private byte[] _png32 = null!;

    [GlobalSetup]
    public void Setup()
    {
        AvaloniaHarness.Ensure();
        _png16 = MakeGlyphPng(16);
        _png32 = MakeGlyphPng(32);
    }

    [Benchmark]
    public bool Tint_16px()
        => TrayIconTint.Process(_png16, Colors.Black)!.Value.Recoloured;

    [Benchmark]
    public bool Tint_32px()
        => TrayIconTint.Process(_png32, Colors.Black)!.Value.Recoloured;

    // Build an N×N premultiplied-BGRA PNG whose centre quarter is opaque white — mirrors the
    // synthetic-PNG helper in TrayIconTintTests so the benchmark exercises the same decode path.
    private static byte[] MakeGlyphPng(int n)
    {
        var wb = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var buf = new byte[n * n * 4];
        int lo = n / 4, hi = n - n / 4;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var i = (y * n + x) * 4;
                byte a = (x >= lo && x < hi && y >= lo && y < hi) ? (byte)255 : (byte)0;
                buf[i] = a;       // B (premultiplied white == a)
                buf[i + 1] = a;   // G
                buf[i + 2] = a;   // R
                buf[i + 3] = a;   // A
            }
        using (var fb = wb.Lock())
            for (var y = 0; y < n; y++)
                Marshal.Copy(buf, y * n * 4, fb.Address + y * fb.RowBytes, n * 4);
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// <see cref="TrayRowsPanel.AssignRows"/> — split N tray items into R rows by balancing each row's
/// total natural width. Pure; re-run on every tray MeasureOverride. 200 items is well past any real
/// tray, included as a stress ceiling.
/// </summary>
[MemoryDiagnoser]
public class TrayRowsBenchmarks
{
    [Params(8, 32, 200)]
    public int Items;

    [Params(1, 3)]
    public int Rows;

    private double[] _widths = null!;

    [GlobalSetup]
    public void Setup()
    {
        _widths = new double[Items];
        for (var i = 0; i < Items; i++)
            _widths[i] = 16 + (i % 5) * 8; // mixed natural widths (16..48)
    }

    [Benchmark]
    public int AssignRows()
        => TrayRowsPanel.AssignRows(_widths, Rows).Length;
}
