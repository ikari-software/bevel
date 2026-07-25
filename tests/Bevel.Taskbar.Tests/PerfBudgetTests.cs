using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Perf BUDGET guards (bevel-zhmr) — coarse regression tripwires for the hot paths, NOT precise
/// measurements (that's benchmarks/Bevel.Benchmarks, run manually). Each budget is ~100–250× the median
/// measured on Apple Silicon, so it survives CI-runner variance yet still fails on a gross regression
/// (an accidental O(n²), a per-call allocation blowup). Numbers + rationale: docs/perf.md.
/// </summary>
public class PerfBudgetTests
{
    // Average wall-clock per op over `iters` after a warmup, so JIT + one-off GC don't dominate.
    private static double AvgMicros(int warmup, int iters, Action op)
    {
        for (var i = 0; i < warmup; i++) op();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iters; i++) op();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
    }

    [Fact]
    public void Perf_grouping_plan_100_windows_under_budget()
    {
        var windows = new List<TaskItemViewModel>(100);
        for (var i = 0; i < 100; i++)
            windows.Add(new TaskItemViewModel(
                new ForeignWindow(new ForeignWindowId("w" + i), "Window " + i,
                    "com.example.app" + (i % 25), false, i == 0, default), Wm));

        // median ~8.3µs → 3000µs budget.
        var us = AvgMicros(20, 200, () => TaskbarGrouping.Plan(windows, grouping: true));
        Assert.True(us < 3000, $"grouping Plan(100) averaged {us:F1}µs (budget 3000µs)");
    }

    [Fact]
    public void Perf_button_layout_is_flat_under_budget()
    {
        // O(1) width/label policy — median ~1.3ns → 50µs budget (huge headroom; catches a pathological rewrite).
        var us = AvgMicros(50, 2000, () => TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, available: 1280, count: 100, rows: 2,
            max: 160, minButtonWidth: 80, labels: TaskbarButtonLabels.Auto));
        Assert.True(us < 50, $"ComputeButtonLayout averaged {us:F2}µs (budget 50µs)");
    }

    [Fact]
    public void Perf_tray_assign_rows_200x3_under_budget()
    {
        var widths = new double[200];
        for (var i = 0; i < widths.Length; i++) widths[i] = 16 + (i % 5) * 8;
        // median ~418ns → 200µs budget.
        var us = AvgMicros(50, 500, () => TrayRowsPanel.AssignRows(widths, 3));
        Assert.True(us < 200, $"AssignRows(200×3) averaged {us:F2}µs (budget 200µs)");
    }

    [AvaloniaFact]
    public void Perf_tray_tint_32px_under_budget()
    {
        var png = MakeGlyphPng(32);
        // median ~16µs → 3000µs budget (decode + full-pixel scan + recolour).
        var us = AvgMicros(10, 100, () => TrayIconTint.Process(png, Colors.Black));
        Assert.True(us < 3000, $"Tint(32px) averaged {us:F1}µs (budget 3000µs)");
    }

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
                buf[i] = a; buf[i + 1] = a; buf[i + 2] = a; buf[i + 3] = a;
            }
        using (var fb = wb.Lock())
            for (var y = 0; y < n; y++)
                Marshal.Copy(buf, y * n * 4, fb.Address + y * fb.RowBytes, n * 4);
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }

    private static readonly IWindowManager Wm = new NoOpWm();

    private sealed class NoOpWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
