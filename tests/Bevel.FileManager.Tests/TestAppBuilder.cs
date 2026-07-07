using Avalonia;
using Avalonia.Headless;
using Bevel.FileManager.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bevel.FileManager.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Bevel.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}