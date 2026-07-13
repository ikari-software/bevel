using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the taskbar at 2 rows, to eyeball the multi-row layout
/// (bevel-0ml) and the data-bound window-button strip (bevel-d2z): Start button + clock span
/// the full height, window buttons wrap across the rows. Dumps a PNG to the scratchpad; not a
/// pixel assertion.
/// </summary>
public class RenderTaskbarTest
{
    [AvaloniaFact]
    public void Render_two_row_taskbar_to_png()
    {
        // Bind the view to the same shape the app uses: a TaskbarViewModel over a ShellModel.
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWindowManager();

        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 2) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Populate the observable Windows collection; the layout pass sizes the buttons. Width /
        // Opacity are seeded to the visible steady state so the render doesn't catch mid-animation.
        for (var i = 0; i < 6; i++)
        {
            var fw = new ForeignWindow(new ForeignWindowId($"w{i}"), $"Window {i + 1}", "App", false, i == 0, default);
            model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        }
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var outPath = Environment.GetEnvironmentVariable("BEVEL_TASKBAR_RENDER_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-taskbar-2row.png");
        frame!.Save(outPath);
    }

    /// <summary>No-op window manager so TaskItemViewModel's activate command has a target.</summary>
    private sealed class StubWindowManager : IWindowManager
    {
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored,
                Notes: Array.Empty<string>(), SupportsReposition: true);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
