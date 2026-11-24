using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-w3jp: the task-menu race guard in <c>TaskbarView.OnTaskButtonContextRequested</c> — a right-click
/// that awaits the tab prefetch must be DROPPED when the projector rebinds the container (or detaches it)
/// during that await; a rebound container would otherwise show app B's menu with app A's tabs.
/// </summary>
public class TaskMenuRaceGuardTests
{
    private static TaskbarView BuildView(ITabProvider tabProvider, params ForeignWindow[] windows)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWm();
        var view = new TaskbarView { DataContext = vm };
        view.Initialize(TestSettings.NoStacks(new BevelSettings
        {
            TaskbarButtonWidth = 150,
            TaskbarGrouping = TaskbarGroupingMode.Never,
        }), tabProvider: tabProvider);
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var w in windows)
            model.Windows.Add(new TaskItemViewModel(w, wm) { Width = 150, Opacity = 1 });
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static ForeignWindow W(string id, string appId)
        => new(new ForeignWindowId(id), id, appId, false, false, default, BundleId: appId);

    private static ToggleButton? FindToggle(Control c)
        => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

    private static int OpenMenuCount(TaskbarView view)
        => TopLevel.GetTopLevel(view)!.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Count();

    [AvaloniaFact]
    public async Task Menu_is_dropped_when_the_button_is_rebound_during_the_tab_prefetch()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<AppTab>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = BuildView(new GatedTabProvider(gate), W("c1", "com.google.Chrome"), W("f1", "com.apple.finder"));
        var vm = (TaskbarViewModel)view.DataContext!;
        var chrome = vm.Items.OfType<TaskItemViewModel>().Single(i => i.BundleId == "com.google.Chrome");
        var finder = vm.Items.OfType<TaskItemViewModel>().Single(i => i.BundleId == "com.apple.finder");
        var button = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(FindToggle).First(b => ReferenceEquals(b?.DataContext, chrome))!;

        // Right-click while the tab prefetch is still in flight: the handler awaits the gate.
        button.RaiseEvent(new ContextRequestedEventArgs
        {
            RoutedEvent = Control.ContextRequestedEvent,
            Source = button,
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, OpenMenuCount(view));

        // The projector rebinds the container mid-await; the fetch then completes.
        button.DataContext = finder;
        gate.SetResult(System.Array.Empty<AppTab>());
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        // Race guard: the click is dropped — no menu for a rebound container.
        Assert.Equal(0, OpenMenuCount(view));

        // Control: the same click WITHOUT a rebind opens the menu (proves the drop above was the guard).
        button.DataContext = chrome;
        button.RaiseEvent(new ContextRequestedEventArgs
        {
            RoutedEvent = Control.ContextRequestedEvent,
            Source = button,
        });
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, OpenMenuCount(view));
    }

    private sealed class GatedTabProvider : ITabProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<AppTab>> _gate;
        public GatedTabProvider(TaskCompletionSource<IReadOnlyList<AppTab>> gate) => _gate = gate;

        public Capabilities Capabilities => new(true, TrayCapability.Mirrored, System.Array.Empty<string>(), true);
        public bool SupportsApp(string? bundleId) => true;
        public async ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
            => await _gate.Task;
        public Task<TabActivationResult> ActivateAsync(AppTab tab, CancellationToken ct = default) => Task.FromResult(TabActivationResult.Activated);
    }

    private sealed class StubWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, System.Array.Empty<string>(), true);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(System.Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;
        public event System.EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event System.EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event System.EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event System.EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
