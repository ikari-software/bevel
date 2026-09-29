using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the Start menu's type-to-search strip (bevel-cezo) in both layouts, so the
/// classic sunken field and the Luna rounded field can be eyeballed. The query is typed through the real
/// event path first, then the popup content is detached and hosted in a window — headless cannot screenshot
/// a live popup. Not a pixel assertion; it proves the searching menu renders and dumps images.
/// </summary>
[Collection("TaskbarTheme")]   // the Luna case mutates Application.Current's theme (bevel-hd05)
public class RenderStartMenuSearchTest
{
    [AvaloniaTheory]
    [InlineData("win2000")]
    [InlineData("luna")]
    public async Task Render_searching_start_menu_to_png(string theme)
    {
        try
        {
            Bevel.UI.ThemeService.Apply(theme);

            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.calc", "Calculator", null, "Utilities"),
                new InstalledApp("com.cale", "Calendar", null, "Productivity"),
                new InstalledApp("com.calm", "Calm Notes", null),
                new InstalledApp("com.term", "Terminal", null, "Developer Tools"));
            using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
            model.Start();
            for (var i = 0; i < 50 && model.Programs.Count < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
            var button = new Button { Content = "Start" };
            var placement = new Window { SystemDecorations = SystemDecorations.None, Content = button };
            placement.Show();
            Dispatcher.UIThread.RunJobs();
            await menu.OpenAsync(button);
            Dispatcher.UIThread.RunJobs();

            // Type through the real routed-event path, so what gets captured is the actual search state.
            var popupRoot = menu.FindControl<Panel>("PopupRoot")!;
            popupRoot.RaiseEvent(new Avalonia.Input.TextInputEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.TextInputEvent,
                Text = "cal",
            });
            Assert.Equal(3, menu.SearchResults.Count);

            // Detach WITHOUT closing (closing would clear the query) and host the content for capture.
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

            var strip = menu.FindControl<Control>(theme == "luna" ? "LunaSearchStrip" : "ClassicSearchStrip")!;
            Assert.True(strip.IsVisible);
            Assert.True(strip.Bounds.Height > 0, $"search strip has no height: {strip.Bounds}");

            // Settle before capturing. A single capture returns null under a full-solution run (test
            // assemblies run in parallel, so the first frame can still be pending) even though the strip
            // above already has real bounds — the same flake class as RenderLunaStartMenuTest, fixed the
            // same way: poll the FRAME itself on a time-based deadline, not the window's bounds.
            Avalonia.Media.Imaging.WriteableBitmap? frame = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                frame = window.CaptureRenderedFrame();
                if (frame is not null && frame.PixelSize.Width > 0) break;
                await Task.Delay(10);
            }

            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_RENDER_OUT")
                          ?? Path.Combine(Path.GetTempPath(), $"bevel-startmenu-search-{theme}.png");
            Bevel.TestSupport.SiteShot.Save(window, outPath);
        }
        finally { Bevel.UI.ThemeService.Apply("win2000"); }
    }
}
