using Avalonia;
using Avalonia.Headless;
using Bevel.Taskbar.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// The whole assembly shares ONE headless Avalonia Application (above) with a real Skia surface, so any
// render-to-png test racing a parallel collection that mutates Application.Current is inherently flaky
// (bevel-hd05, and the broader render-capture flakiness). Serialize the assembly — correctness over the
// few seconds of lost parallelism. The per-class [Collection("TaskbarTheme")] tags remain as documentation.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Bevel.Taskbar.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Bevel.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}