using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        // Start the host so IHostedServices run (e.g. the macOS HelperLifecycle). This is
        // non-blocking — hosted services degrade gracefully rather than aborting boot.
        host.Start();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop hosted services cleanly on exit (kills the helper, ends the monitor).
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    // Referenced by name by the Avalonia XAML previewer/designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
