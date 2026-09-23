using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-c54t — the Win10/11 big-icons bar. Before this, <c>Large</c> only grew the chrome: the task
/// button's Image was a hardcoded 16×16 in the template, so a taller bar just meant taller empty
/// buttons. The glyph edge now comes from the <c>Bevel.Metric.TaskbarIconSize</c> token, published from
/// <see cref="TaskbarTheme.TaskIconSize"/>, so the realized Image really is 24 px under Large and 32 px
/// under Big — and it re-sizes LIVE when the tier changes through the settings path, without rebuilding
/// the strip.
///
/// <para>The source PNG is 64×64 (the size the macOS helper renders app icons at), so every tier is a
/// DOWNSCALE with HighQuality interpolation — nothing is an upscaled 16 px bitmap.</para>
/// </summary>
[Collection("TaskbarTheme")]   // mutates the shared TaskbarTheme metrics + Application.Current resources
public class RenderBigIconsTaskbarTest
{
    /// <summary>A 64×64 RGBA app-icon stand-in — the same source size the macOS helper emits, so the
    /// bigger tiers downscale it rather than upscaling a 16px bitmap.</summary>
    private static readonly byte[] AppIcon64 = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAACUElEQVR42u3bZU8VcBzFcV6SM2bMmDFjxowZMyYgISEhISEhISEh" +
        "4QUkJCQkJCQk7H4P570crw/uducDLsdH7tx7tt8L+Hz/j/9xcbHFFlv4tsWD2xNAN1d+B1jQGcEVwu9IBHfe8YkQwhc+A4sCm7jC" +
        "8buSwN3J4J4UcG8quO8uuD8NPBC8g+ngoQzwcCZ45B54NAs8lg0ezwFP5IIn74On8sDT+eCZAvBsIXjuAXi+CLwQvIvF4KUS8HIp" +
        "eOUheLUMvFYOXq8Ab1SCNx+Bt6rA29VgfA2YUAsmPgaT6sDk4KXUg6kNYFojmP4EzGgCM5vBrBYwuxXMeQrmtoF57eDf+OJAhIeN" +
        "anxoUY3/M0d8SZcQwBFf2i0EcMSX9QgBHPHlz4UAjviKXiGAI76yTwjgiK/qFwI44qsHhACO+JoXQgBHfO2gEMARXzckBHDE1w8L" +
        "ARzxDSNCAEd840shgCO+aVQI4IhvVgI44lvGhACO+NZxIYAjvm1CCOCIb38lBHDEd0wKARzxnVNCAEd8YFoI4IjvmhECOOK7XwsB" +
        "HPE9s0IAR3zvnBDAEd83LwRwxPcvCAEc8QNvhACO+MFFIYAjfkgJ4IgfXhICOOJHloUAjvjRFSGAI37srRDAET++KgRwxE+sCQEc" +
        "8ZPrQgBH/NSGEMARP/1OCOCIn3kvBHDEz34QAjji5z4KARzx85+EAI74hc9CAEf84hchgCN+SQngiF/+usUArviVb+Dq9wgR3PFr" +
        "P8D1n5tE+Oc/Nv/5wvEbvyK43PChbQkfW2zRt98voG3La2mntgAAAABJRU5ErkJggg==");

    /// <summary>Realized task-button glyph slots in the strip, in logical px.</summary>
    private static double[] IconSlots(TaskbarView view) =>
        view.WindowButtonAreaControl.GetRealizedContainers()
            .SelectMany(c => c.GetVisualDescendants().OfType<Image>())
            .Select(i => i.Width)
            .ToArray();

    private static (TaskbarWindow Window, TaskbarView View, ShellModel Model) BuildBar(int rows = 1)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 5; i++)
        {
            var fw = new ForeignWindow(
                new ForeignWindowId($"w{i}"), $"Window {i + 1}", "App", false, i == 0, default, AppIcon64);
            // ShellModel's own enumeration path decodes ForeignWindow.IconPng off-thread; a hand-built VM
            // gets the same 64px bitmap assigned directly so the render shows a real (downscaled) glyph.
            model.Windows.Add(new TaskItemViewModel(fw, null!)
            {
                Width = 48,
                Opacity = 1,
                IconSource = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(AppIcon64)),
            });
        }
        Dispatcher.UIThread.RunJobs();
        return (window, view, model);
    }

    [AvaloniaFact]
    public void Task_icon_slot_follows_the_button_size_tier()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
            var (_, view, _) = BuildBar();
            Assert.All(IconSlots(view), w => Assert.Equal(16, w));

            // Live tier change through the same entry point the 750 ms settings poll uses. No restart,
            // no strip rebuild — the DynamicResource token re-measures the existing Images.
            view.ApplyLiveSettings(new BevelSettings { TaskbarButtonSize = TaskbarButtonSize.Large });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(24, TaskbarTheme.TaskIconSize);
            var large = IconSlots(view);
            Assert.NotEmpty(large);
            Assert.All(large, w => Assert.Equal(24, w));

            view.ApplyLiveSettings(new BevelSettings { TaskbarButtonSize = TaskbarButtonSize.Big });
            Dispatcher.UIThread.RunJobs();
            var big = IconSlots(view);
            Assert.NotEmpty(big);
            Assert.All(big, w => Assert.Equal(32, w));

            // Clearly larger than Normal — the whole point of the bead: not just taller empty chrome.
            Assert.True(big[0] > 16 * 1.5);

            // Small must still be the classic 16px glyph (no regression at the other end).
            view.ApplyLiveSettings(new BevelSettings { TaskbarButtonSize = TaskbarButtonSize.Small });
            Dispatcher.UIThread.RunJobs();
            Assert.All(IconSlots(view), w => Assert.Equal(16, w));
        }
        finally { TaskbarTheme.Configure(TaskbarButtonSize.Normal); }
    }

    /// <summary>The bar height (and therefore the reserved work area) follows the tier through the ONE
    /// authority — <see cref="TaskbarTheme.HeightForRows"/> — for single and multi-row bars alike.</summary>
    [AvaloniaFact]
    public void Big_icons_grow_the_bar_height_for_one_and_two_rows()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
            var normal1 = TaskbarTheme.HeightForRows(1);
            var normal2 = TaskbarTheme.HeightForRows(2);

            var (window, view, _) = BuildBar(rows: 2);
            view.ApplyLiveSettings(new BevelSettings { TaskbarButtonSize = TaskbarButtonSize.Big, TaskbarRows = 2 });
            Dispatcher.UIThread.RunJobs();

            Assert.True(TaskbarTheme.HeightForRows(1) > normal1);
            Assert.True(TaskbarTheme.HeightForRows(2) > normal2);
            // ReapplyMetrics pins the window to the ONE height authority (HeightForRows) and refreshes the
            // work-area band from it — no forked height maths for the big tier.
            Assert.Equal(TaskbarTheme.HeightForRows(2), window.MinHeight);
            Assert.Equal(TaskbarTheme.HeightForRows(2), window.MaxHeight);
        }
        finally { TaskbarTheme.Configure(TaskbarButtonSize.Normal); }
    }

    /// <summary>
    /// Visual harvest: the icon-only Win10/11 bar (Big + IconOnly). Dumps a real render to the
    /// scratchpad — the shot `site/index.html` should carry for this mode rather than a hand-drawn one.
    /// </summary>
    [AvaloniaFact]
    public void Render_big_icons_taskbar_to_png()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Big);
            var (window, view, _) = BuildBar();
            view.ApplyLiveSettings(new BevelSettings
            {
                TaskbarButtonSize = TaskbarButtonSize.Big,
                TaskbarButtonLabels = TaskbarButtonLabels.IconOnly,
            });
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_BIGICONS_TASKBAR_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-taskbar-big-icons.png");
            frame!.Save(outPath);
        }
        finally { TaskbarTheme.Configure(TaskbarButtonSize.Normal); }
    }
}
