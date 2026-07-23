using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void Render_luna_taskbar_to_png()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            var model = new ShellModel(null, null, null);
            var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
            var wm = new StubWindowManager();
            var view = new TaskbarView { DataContext = vm };
            var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 2; i++)
            {
                var fw = new ForeignWindow(new ForeignWindowId($"w{i}"), $"Window {i + 1}", "App", false, i == 0, default);
                model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
            }
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_TASKBAR_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-taskbar.png");
            frame!.Save(outPath);
        }
        finally { Bevel.UI.ThemeService.Apply("win2000"); }
    }

    [AvaloniaFact]
    public void Overflow_chevrons_are_flat_vector_arrows_not_classic_buttons()
    {
        // The row-overflow scroll buttons used to be default Classic RepeatButtons with a "▲"/"▼" font
        // glyph — grey and ugly on the themed bar. They must now be flat "chevron"-styled buttons whose
        // content is a vector Path arrow (themed fill via Bevel.Brush.TrayText), so they read cleanly on
        // both the Win2000 grey and the Luna blue.
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 2) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "ScrollUpBtn", "ScrollDownBtn" })
        {
            var btn = view.FindControl<RepeatButton>(name);
            Assert.NotNull(btn);
            Assert.Contains("chevron", btn!.Classes);                          // flat styled, not a Classic button
            Assert.IsType<Avalonia.Controls.Shapes.Path>(btn.Content);         // vector arrow, not a font glyph
        }
    }

    /// <summary>
    /// Regression: the sunken/pressed state is bound OneWay to <see cref="TaskItemViewModel.IsFocused"/>,
    /// but a ToggleButton flips IsChecked locally on click. If the clicked window doesn't take focus,
    /// that local flip must not stick — otherwise several buttons show pressed at once (the "3 buttons
    /// pressed" bug). The click handler snaps IsChecked back to IsFocused.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_an_unfocused_task_button_does_not_leave_it_stuck_pressed()
    {
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWindowManager();

        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // One UNFOCUSED window.
        var fw = new ForeignWindow(new ForeignWindowId("w0"), "Window", "App", false, false, default);
        model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        Dispatcher.UIThread.RunJobs();

        var button = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(c => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault())
            .FirstOrDefault(b => b is not null);
        Assert.NotNull(button);

        // Simulate the ToggleButton's local self-toggle on click (IsChecked -> true) while the window
        // is not focused, then raise Click as the real gesture does.
        button!.IsChecked = true;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // Not focused → must not stay pressed.
        Assert.False(button.IsChecked);
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
