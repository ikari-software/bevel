using Avalonia;
using Avalonia.Headless;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Bevel.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        // Bring up the headless Skia platform ONCE for this process. Several hot paths decode or
        // rasterize an Avalonia Bitmap (TrayIconTint, the vector glyphs), and BenchConfig pins an
        // in-process toolchain so every benchmark runs in this same, already-initialized process
        // (an out-of-process toolchain would re-enter Main per benchmark and re-run this).
        AvaloniaHarness.Ensure();

        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance))
            .AddDiagnoser(MemoryDiagnoser.Default);

        // No args → run everything non-interactively (CI/one-shot); otherwise honour BDN's filters.
        if (args.Length == 0)
            args = new[] { "--filter", "*" };

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}

/// <summary>One-time headless-Avalonia + Skia bootstrap, safe to call more than once per process.</summary>
internal static class AvaloniaHarness
{
    private static readonly object Gate = new();
    private static bool _ready;

    public static void Ensure()
    {
        if (_ready) return;
        lock (Gate)
        {
            if (_ready) return;
            AppBuilder.Configure<Bevel.App.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            _ready = true;
        }
    }
}
