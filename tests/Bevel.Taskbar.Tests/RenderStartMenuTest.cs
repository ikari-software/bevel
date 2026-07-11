using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the Start menu to a PNG, so its Win2000 fidelity can be eyeballed
/// without fighting live popup/window-level hosting. Not a pixel assertion — it just proves the
/// menu content renders and dumps an image to the scratchpad.
/// </summary>
public class RenderStartMenuTest
{
    [AvaloniaFact]
    public void Render_start_menu_to_png()
    {
        var menu = new StartMenu();

        // Detach the popup content so we can host it directly in a window and capture it
        // (a Popup renders into its own top-level, which headless can't screenshot easily).
        var content = (Control)menu.MenuPopupControl.Child!;
        menu.MenuPopupControl.Child = null;

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Hover the "Documents" row to exercise the navy full-row selection + white text.
        window.MouseMove(new Point(110, 34));
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 120, $"menu too narrow: {frame.PixelSize}");

        var outPath = Environment.GetEnvironmentVariable("BEVEL_RENDER_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-startmenu-render.png");
        frame.Save(outPath);
    }
}
