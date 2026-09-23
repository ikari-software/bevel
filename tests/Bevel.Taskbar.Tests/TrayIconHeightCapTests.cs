using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Tray icons must fit the row they live in (bevel-xpfl). A mirrored menu-bar item renders at its NATIVE
/// on-screen size × the user's size slider, and neither of those is bounded by the taskbar: the helper
/// admits status items 8–40pt tall (a notched Mac's menu-bar band is ~37pt), so on such a machine a
/// two-row bar wanted 2×37pt of tray inside a 58pt bar and the icons spilled past it. The cap is derived
/// from the live <see cref="TaskbarTheme"/> metrics, so it follows the Small/Normal/Large tier.
///
/// Reads the shared <see cref="TaskbarTheme"/> metrics, hence the serial theme collection.
/// </summary>
[Collection("TaskbarTheme")]
public class TrayIconHeightCapTests
{
    /// <summary>A notch-Mac-sized status item: 30pt wide, 37pt tall.</summary>
    private static TrayItem Tall(string id) =>
        new(new TrayItemId(id), id, Bounds: new PalRect(0, 0, 30, 37));

    [AvaloniaFact]
    public void Tall_menu_bar_items_are_capped_to_the_row_height_on_a_two_row_bar()
    {
        var vm = new TrayViewModel(new StubTray(Tall("1:10"), Tall("2:20")));
        vm.Start();
        Dispatcher.UIThread.RunJobs();
        vm.SetRows(2);

        var cap = TrayViewModel.MaxIconHeight(2);
        Assert.Equal(26, cap);                       // (58pt bar − 6pt well chrome) / 2 rows
        Assert.All(vm.Items, i => Assert.Equal(cap, i.IconH, 3));

        // Clamped uniformly — the icon shrinks, it does not squash: 30/37 aspect survives.
        Assert.All(vm.Items, i => Assert.Equal(30.0 / 37.0, i.IconW / i.IconH, 3));
    }

    [AvaloniaFact]
    public void A_single_row_bar_caps_the_same_way()
    {
        var vm = new TrayViewModel(new StubTray(Tall("1:10")));
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(24, TrayViewModel.MaxIconHeight(1));   // 30pt bar − 6pt well chrome
        Assert.Equal(24, vm.Items[0].IconH, 3);
    }

    [AvaloniaFact]
    public void The_size_slider_still_scales_but_only_up_to_the_cap()
    {
        // A small native item (14pt tall) has headroom: the slider scales it exactly.
        var vm = new TrayViewModel(new StubTray(new TrayItem(new TrayItemId("1:10"), "s",
            Bounds: new PalRect(0, 0, 20, 14))));
        vm.Start();
        Dispatcher.UIThread.RunJobs();
        vm.SetRows(2);

        vm.Configure(overflowCap: 8, iconSize: 24);          // 1.5× → 21pt, under the 26pt cap
        Assert.Equal(21, vm.Items[0].IconH, 3);
        Assert.Equal(30, vm.Items[0].IconW, 3);

        vm.Configure(overflowCap: 8, iconSize: 32);          // 2.0× → 28pt, over the cap → clamped
        Assert.Equal(TrayViewModel.MaxIconHeight(2), vm.Items[0].IconH, 3);
        Assert.Equal(20.0 / 14.0, vm.Items[0].IconW / vm.Items[0].IconH, 3);
    }

    // Integration gap found when bevel-c54t merged: the tier test above predates the Big tier, so the
    // tray budget was unasserted on the tallest bar. Big is the interesting case in the other direction —
    // the band finally has room for a real notch-Mac item, so the cap must STOP clamping rather than
    // keep shrinking it, while a slider value beyond the budget is still clamped.
    [AvaloniaFact]
    public void The_big_icons_tier_gives_the_tray_room_instead_of_clamping()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Big);        // button 40 → row 44 → bar 46
            var vm = new TrayViewModel(new StubTray(Tall("1:10")));
            vm.Start();
            Dispatcher.UIThread.RunJobs();
            vm.SetRows(2);

            // HeightForRows(2) = 46 + 44 = 90; (90 − 6) / 2 = 42 — bigger than the 26pt Normal budget.
            Assert.Equal(42, TrayViewModel.MaxIconHeight(2));
            Assert.True(TrayViewModel.MaxIconHeight(2) > 26, "Big must widen the tray budget, not narrow it");

            // A 37pt item now fits under the 42pt budget, so it is passed through UNCLAMPED at full size.
            Assert.Equal(37, vm.Items[0].IconH, 3);
            Assert.Equal(30.0 / 37.0, vm.Items[0].IconW / vm.Items[0].IconH, 3);

            // The cap is still a real ceiling on this tier: an oversized slider value gets clamped to it.
            vm.Configure(overflowCap: 8, iconSize: 32);           // 2.0× of 37 = 74pt, over the 42pt budget
            Assert.Equal(42, vm.Items[0].IconH, 3);
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);     // restore the shared default
        }
    }

    [AvaloniaFact]
    public void The_cap_follows_the_button_height_tier()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Small);     // row 22 → bar 46 at 2 rows
            var vm = new TrayViewModel(new StubTray(Tall("1:10")));
            vm.Start();
            Dispatcher.UIThread.RunJobs();
            vm.SetRows(2);

            Assert.Equal(20, TrayViewModel.MaxIconHeight(2));    // (46 − 6) / 2
            Assert.Equal(20, vm.Items[0].IconH, 3);
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);    // restore the shared default
        }
    }

    [AvaloniaFact]
    public void Tray_strip_lays_out_inside_a_two_row_bar()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var tray = new StubTray(Tall("1:10"), Tall("2:20"), Tall("3:30"), Tall("4:40"));
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), tray: tray);

        var view = new TaskbarView { DataContext = vm };
        view.Initialize(new BevelSettings { TaskbarRows = 2 });
        var window = new TaskbarWindow(null, rows: 2) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var barHeight = TaskbarTheme.HeightForRows(2);
        var panel = view.GetVisualDescendants().OfType<TrayRowsPanel>().Single();
        Assert.Equal(4, panel.Children.Count);

        // The strip itself fits the bar…
        Assert.True(panel.Bounds.Height <= barHeight,
            $"tray strip {panel.Bounds.Height}pt must fit the {barHeight}pt bar");

        // …and so does every icon in it, in the window's own coordinate space.
        foreach (var child in panel.Children)
        {
            var top = child.TranslatePoint(default, (Visual)window);
            Assert.NotNull(top);
            Assert.True(top!.Value.Y >= -0.01, $"tray icon spills above the bar (y={top.Value.Y})");
            Assert.True(top.Value.Y + child.Bounds.Height <= barHeight + 0.01,
                $"tray icon spills past the bar: {top.Value.Y} + {child.Bounds.Height} > {barHeight}");
            Assert.True(child.Bounds.Height <= TrayViewModel.MaxIconHeight(2) + 0.01,
                $"tray icon {child.Bounds.Height}pt exceeds the per-row cap");
        }
    }

    private sealed class StubTray : ISystemTrayHost
    {
        private readonly List<TrayItem> _items;
        public StubTray(params TrayItem[] items) => _items = items.ToList();

        public Capabilities Capabilities => Capabilities.None;
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(_items.ToArray());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
            CancellationToken ct = default) => Task.FromResult(true);

        // Never raised here — the initial snapshot is all these tests need.
        public event EventHandler<TrayItem>? ItemAdded { add { } remove { } }
        public event EventHandler<TrayItem>? ItemRemoved { add { } remove { } }
        public event EventHandler<TrayItem>? ItemUpdated { add { } remove { } }
    }
}
