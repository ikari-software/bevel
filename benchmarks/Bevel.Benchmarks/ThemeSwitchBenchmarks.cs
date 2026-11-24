using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.UI;
using Bevel.UI.Blue2001;

namespace Bevel.Benchmarks;

/// <summary>
/// bevel-yd2n profile: one Blue2001 colourway switch (Blue↔Silver, Hybrid gloss) timed on the UI
/// thread with a window of DynamicResource consumers realized, so every per-key resource-changed
/// notification in <see cref="Blue2001VariantService.Apply"/> reaches real consumers. Budget (the
/// bead): one switch ≤ 50 ms wall.
/// NOT a BDN [Benchmark]: constructing an AvaloniaObject VerifyAccess()es the UI thread, and BDN's
/// engine thread is not it (a [Benchmark] here NA's with "Call from invalid thread"). Instead run
///   dotnet run -c Release --project benchmarks/Bevel.Benchmarks -- --measure-theme-switch
/// which brings the headless app up on the main thread (Program.Main) and times a plain loop here.
/// </summary>
internal static class ThemeSwitchBenchmarks
{
    private const int Warmup = 5;
    private const int Iterations = 30;
    private const int ConsumerRows = 60;   // 60 rows × 4 DynamicResource bindings ≈ a taskbar + filer's worth

    public static void Run()
    {
        AvaloniaHarness.Ensure();
        Blue2001VariantService.Clear();

        // A realized window whose controls consume the keys the variant rewrites — without this the
        // per-write notifications have no subscribers and the measurement would be meaningless.
        var keys = new[] { ThemeTokens.BrushWindow, ThemeTokens.BrushButtonFace, "Bevel.Brush.Highlight", "Bevel.Brush.TrayText" };
        var rows = new StackPanel();
        for (var i = 0; i < ConsumerRows; i++)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            for (var k = 0; k < keys.Length; k++)
            {
                var b = new Border { Width = 24, Height = 24, Child = new TextBlock { Text = "x" } };
                b[!Border.BackgroundProperty] = new DynamicResourceExtension(keys[k]);
                b[!Border.BorderBrushProperty] = new DynamicResourceExtension(keys[(k + 1) % keys.Length]);
                ((TextBlock)b.Child!)[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(keys[(k + 2) % keys.Length]);
                row.Children.Add(b);
            }
            rows.Children.Add(row);
        }
        var window = new Window { Width = 800, Height = 600, Content = rows };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var times = new List<double>(Iterations);
        var toSilver = false;
        for (var i = 0; i < Warmup; i++)
        {
            toSilver = !toSilver;
            Blue2001VariantService.Apply(toSilver ? "Silver" : "Blue", "Hybrid");
            Dispatcher.UIThread.RunJobs();
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = new Stopwatch();
        for (var i = 0; i < Iterations; i++)
        {
            toSilver = !toSilver;
            var color = toSilver ? "Silver" : "Blue";
            sw.Restart();
            Blue2001VariantService.Apply(color, "Hybrid");
            Dispatcher.UIThread.RunJobs();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var allocPerOp = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / (double)Iterations;

        window.Close();
        Blue2001VariantService.Clear();

        times.Sort();
        var mean = times.Average();
        Console.WriteLine($"Blue2001_colourway_switch (Blue<->Silver, {ConsumerRows * 4} DynamicResource consumers realized):");
        Console.WriteLine($"  n={Iterations} warmup={Warmup}  mean={mean:F3} ms  median={times[Iterations / 2]:F3} ms  " +
                          $"p95={times[(int)(Iterations * 0.95)]:F3} ms  max={times[^1]:F3} ms  alloc={allocPerOp / 1024.0:F1} KiB/op");
        Console.WriteLine(mean <= 50.0
            ? "  => UNDER the 50 ms budget: no rewrite (bevel-yd2n closes as measured)."
            : "  => OVER the 50 ms budget: the single-dictionary swap is warranted.");
    }
}
