using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-corepulse: the lost-core indicator. A taskbar that has lost its link to the core shows no
/// window buttons and no tray — which reads as "my settings were wiped" rather than "the core is
/// unreachable", and a real incident was misdiagnosed that way. The indicator therefore has to be
/// noticeable (it pulses) and actionable (it offers the repairs), not a silent red dot.
/// </summary>
[Collection("TaskbarTheme")]
public class DisconnectedIndicatorTests
{
    private sealed class Link : IShellConnectionStatus
    {
        public bool IsConnected { get; private set; }
        public event EventHandler<bool>? ConnectionChanged;
        public Link(bool connected) => IsConnected = connected;
        public void Set(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, connected);
        }
    }

    private static (TaskbarView View, TaskbarViewModel Vm, Link Link) Build(bool connected)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var link = new Link(connected);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), link);
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, vm, link);
    }

    private static Grid Indicator(TaskbarView view) =>
        Assert.Single(view.GetVisualDescendants().OfType<Grid>(),
            g => g.Name == "DisconnectedIndicator");

    [AvaloniaFact]
    public void Indicator_is_hidden_while_the_core_link_is_healthy()
    {
        var (view, _, _) = Build(connected: true);
        Assert.False(Indicator(view).IsVisible);
    }

    [AvaloniaFact]
    public void Indicator_appears_when_the_core_link_drops()
    {
        var (view, _, link) = Build(connected: true);
        link.Set(false);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Indicator(view).IsVisible);
    }

    [AvaloniaFact]
    public void Solid_dot_stays_fully_opaque_so_the_pulse_never_costs_legibility()
    {
        var (view, _, _) = Build(connected: false);
        var ellipses = Indicator(view).GetVisualDescendants().OfType<Ellipse>().ToList();

        // The halo is the only animated part; the dot beneath it must not be faded by the pulse.
        var halo = Assert.Single(ellipses, e => e.Classes.Contains("halo"));
        var dot = Assert.Single(ellipses, e => !e.Classes.Contains("halo"));
        Assert.Equal(1.0, dot.Opacity, 3);
        Assert.NotNull(halo.RenderTransform);   // the ping scales; without a transform it cannot
    }

    [AvaloniaFact]
    public void Repair_actions_are_offered_and_gated_on_being_able_to_run()
    {
        var (view, _, _) = Build(connected: false);

        // Unsupervised: no launcher to respawn the core, so that repair must not be offered as if it
        // would work — silently doing nothing is worse than not offering it.
        view.Initialize(new Core.BevelSettings(), restart: () => { }, restartCore: null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.RepairRestartCoreItem.IsEnabled);
        Assert.True(view.RepairRestartShellItem.IsEnabled);

        // Supervised: both repairs available.
        view.Initialize(new Core.BevelSettings(), restart: () => { }, restartCore: () => { });
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.RepairRestartCoreItem.IsEnabled);
        Assert.True(view.RepairRestartShellItem.IsEnabled);
    }

    [AvaloniaFact]
    public void Choosing_restart_core_invokes_the_targeted_repair_not_the_whole_shell()
    {
        var (view, _, _) = Build(connected: false);
        var core = 0;
        var all = 0;
        view.Initialize(new Core.BevelSettings(), restart: () => all++, restartCore: () => core++);
        Dispatcher.UIThread.RunJobs();

        view.RepairRestartCoreItem.RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, core);
        Assert.Equal(0, all);   // the targeted repair must not escalate to a full shell restart
    }
}
