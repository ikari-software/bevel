using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>bevel-m2.15: the in-process app/file icon render (NSWorkspace + CoreGraphics).</summary>
public class MacOSIconProviderTests
{
    [Fact]
    public async Task Renders_a_real_app_icon_to_nonblank_bgra()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only.

        var app = new[]
        {
            "/System/Applications/Calculator.app",
            "/System/Library/CoreServices/Finder.app",
            "/Applications/Safari.app",
        }.FirstOrDefault(Directory.Exists);
        if (app is null)
            return; // no known app on this system.

        var provider = new MacOSIconProvider();
        var img = await provider.GetIconAsync(app, 16);

        Assert.Equal(16, img.Width);
        Assert.Equal(16, img.Height);
        Assert.Equal(16 * 16 * 4, img.Bgra.Length);
        // A real app icon draws some non-transparent pixels.
        Assert.Contains(img.Bgra, b => b != 0);
    }

    [Fact]
    public async Task Missing_path_does_not_throw_and_has_correct_shape()
    {
        var provider = new MacOSIconProvider();
        // NSWorkspace hands back a generic icon for unknown paths; the contract we assert
        // is only that it never throws and returns a correctly shaped buffer.
        var img = await provider.GetIconAsync("/no/such/thing-" + Guid.NewGuid().ToString("N") + ".app", 16);

        Assert.Equal(16, img.Width);
        Assert.Equal(16 * 16 * 4, img.Bgra.Length);
    }

    [Fact]
    public async Task Caches_by_path_and_size()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var app = new[] { "/System/Applications/Calculator.app", "/System/Library/CoreServices/Finder.app" }
            .FirstOrDefault(Directory.Exists);
        if (app is null)
            return;

        var provider = new MacOSIconProvider();
        var a = await provider.GetIconAsync(app, 16);
        var b = await provider.GetIconAsync(app, 16);
        Assert.Same(a.Bgra, b.Bgra); // second call returns the cached PalImage
    }
}
