using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
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

    /// <summary>
    /// Exercises the *bound* Programs cascade end to end: a real <see cref="ShellModel"/> reconciles
    /// two installed apps into <see cref="StartMenuViewModel.Programs"/>, the menu binds them via
    /// <see cref="StartMenu"/>'s ItemsSource + ContainerPrepared wiring, and opening the Programs
    /// submenu must realize a container per program carrying its DisplayName header. A VM-only
    /// assertion would pass even if that wiring silently broke (avalonia-popup-needs-visual-tree),
    /// so this drives the real container realization and captures pixels.
    /// </summary>
    [AvaloniaFact]
    public async Task Bound_programs_cascade_realizes_containers_from_the_view_model()
    {
        var appEnv = new StubAppEnvironment(
            new InstalledApp("com.a", "Alpha", null),
            new InstalledApp("com.b", "Beta", null));
        using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start(); // off-thread enumerate → reconcile onto the UI thread
        var vm = new StartMenuViewModel(model);

        for (var i = 0; i < 50 && model.Programs.Count < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Equal(2, model.Programs.Count);

        var menu = new StartMenu(null, null, programs: vm);
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

        // Open the Programs submenu so its bound children realize and ContainerPrepared runs.
        var programsItem = menu.FindControl<MenuItem>("ProgramsItem");
        Assert.NotNull(programsItem);
        programsItem!.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        // Each realized container is a MenuItem whose Header was set from the program VM.
        var headers = programsItem.Items
            .OfType<object>()
            .Select(programsItem.ContainerFromItem)
            .OfType<MenuItem>()
            .Select(m => m.Header as string)
            .Where(h => h is not null)
            .ToList();

        Assert.Equal(["Alpha", "Beta"], headers);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }
}
