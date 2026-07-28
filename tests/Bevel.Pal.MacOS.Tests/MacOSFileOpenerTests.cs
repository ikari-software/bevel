using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>bevel-wxt: the local "Open With" handler enumeration (NSWorkspace LaunchServices) that
/// feeds the Explorer's Open-With submenu. On-device only — it hits real AppKit.</summary>
public class MacOSFileOpenerTests
{
    [Fact]
    public async Task GetHandlers_lists_real_apps_for_a_text_file()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only — needs LaunchServices.

        var file = Path.Combine(Path.GetTempPath(), "bevel-openwith-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "hello");
        try
        {
            var opener = new MacOSFileOpener();
            var handlers = await opener.GetHandlersAsync(file);

            // Every macOS ships something that opens .txt (TextEdit at minimum), so the list is non-empty…
            Assert.NotEmpty(handlers);
            // …each row points at a real .app bundle on disk with a display name…
            Assert.All(handlers, h =>
            {
                Assert.False(string.IsNullOrWhiteSpace(h.AppName));
                Assert.EndsWith(".app", h.AppPath);
                Assert.True(Directory.Exists(h.AppPath), $"handler app should exist: {h.AppPath}");
            });
            // …and exactly the default handler (if any) is flagged, and it sorts first.
            var defaults = handlers.Where(h => h.IsDefault).ToList();
            Assert.True(defaults.Count <= 1);
            if (defaults.Count == 1)
                Assert.True(handlers[0].IsDefault, "the default handler should sort to the top");
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public async Task GetHandlers_returns_empty_for_blank_path_without_throwing()
    {
        var opener = new MacOSFileOpener();
        var handlers = await opener.GetHandlersAsync("   ");
        Assert.Empty(handlers);
    }
}
