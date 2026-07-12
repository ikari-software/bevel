using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

namespace Bevel.App;

internal static class Program
{
    // Avalonia needs an STA thread on Windows; harmless elsewhere.
    [STAThread]
    public static void Main(string[] args)
    {
        var pal = PalSelector.FromArgs(args);

        // Compose via Microsoft.Extensions.Hosting (DI-01). The PAL is selected here,
        // at the composition root, and nowhere else (DI-02).
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services
            .AddBevelPlatform(pal)
            .AddBevelModules();

        using var host = builder.Build();
        App.Services = host.Services;

        // Let a termination signal (SIGTERM / SIGINT / Ctrl-C) drive a clean Avalonia
        // shutdown so window OnClosed handlers and hosted-service Dispose run (e.g. the
        // Dock controller restores the user's Dock preference). .NET on macOS does NOT
        // surface SIGTERM via Console.CancelKeyPress/AppDomain.ProcessExit, so register a
        // POSIX signal handler explicitly (bevel-3kz).
        var appBuilder = BuildAvaloniaApp();
        // Keep the registrations rooted for the whole process lifetime: PosixSignalRegistration
        // unregisters its handler once the instance is garbage-collected.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => App.RequestExit());
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => App.RequestExit());

        // Start the host so IHostedServices run (e.g. the macOS HelperLifecycle). This is
        // non-blocking — hosted services degrade gracefully rather than aborting boot.
        host.Start();
        try
        {
            appBuilder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop hosted services cleanly on exit (kills the helper, restores the Dock).
            // Run on the thread pool, NOT this thread: after the desktop lifetime returns,
            // Avalonia's SynchronizationContext is still installed here, so a plain
            // .GetResult() would post StopAsync's await-continuations back to this blocked
            // main thread and deadlock — the helper never stops and the process hangs on
            // SIGTERM (only kill -9 works). Task.Run detaches from that context (bevel-fu5).
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        }
    }

    // Referenced by name by the Avalonia XAML previewer/designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
