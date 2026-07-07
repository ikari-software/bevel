using Avalonia;
using Avalonia.Headless;
using Bevel.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bevel.UI.Tests;

/// <summary>Boots the real <see cref="Bevel.App.App"/> (incl. the Win2000 theme) headlessly.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Bevel.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
