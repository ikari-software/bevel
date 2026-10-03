using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 14 fix round 2, Finding 1: proves <see cref="TaskbarView.ApplyComponentRegion"/>
/// ITSELF — the real production call site, not a test's own stand-in filter — keeps ghost inert
/// placeholders out of the stacks region when handed the FULL migrated component list (Start,
/// WindowStrip, Stack, Tray, Clock, ShowDesktop). The round-1 regression test for this
/// (<c>ComponentBarHostTests.A_full_migrated_list_filtered_to_served_types_produces_only_those_slots</c>)
/// filtered the list itself before calling <see cref="ComponentBarHost.ApplyAsync"/> directly, so it
/// would still pass even if <c>ApplyComponentRegion</c>'s own <c>.Where(...)</c> clause were deleted —
/// exactly the gap that let the original Critical through. This test drives the real method via the
/// <c>InternalsVisibleTo</c> seam <c>Bevel.Taskbar.csproj</c> already grants this assembly (the same
/// one <c>WorkAreaMitigator</c> and <c>RemoteComponentChannel.OnBusMessage</c> rely on).
/// </summary>
[Collection("TaskbarTheme")]
public class ComponentRegionFilterTests
{
    [AvaloniaFact]
    public async Task ApplyComponentRegion_filters_a_full_migrated_list_to_only_stack_slots()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };

        // Initialize with an empty component list; OnLoaded's InitComponentRegion builds the real
        // host and runs one (empty) ApplyComponentRegion pass, exactly as the live app does.
        view.Initialize(new BevelSettings());
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = view.ComponentHost;
        Assert.NotNull(host);

        // The REALISTIC full list a real settings.db produces: Start, WindowStrip, Stack, Tray,
        // Clock and ShowDesktop. This region's registry (wired inside InitComponentRegion) resolves
        // only Stack — if ApplyComponentRegion handed this over unfiltered, every other type would
        // come back from the normalizer as "unknown" and render as a ghost inert placeholder.
        var full = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        Assert.True(full.Select(i => i.TypeId).Distinct().Count() > 1,
            "the default migrated list should contain more than one component type");

        view.ApplyComponentRegion(new BevelSettings { TaskbarComponents = full });

        // ApplyComponentRegion fires ApplyAsync via Task.Run and returns immediately; pump the
        // dispatcher (bounded) until the background run's final UI-thread hop has landed.
        await Pump(() => host!.Slots.Count > 0, TimeSpan.FromSeconds(5));

        Assert.NotEmpty(host!.Slots);   // the default Downloads stack resolves to a live slot
        Assert.All(host.Slots, s => Assert.Equal(TaskbarComponentTypes.Stack, s.TypeId));
        Assert.All(host.Slots, s => Assert.False(s.IsInert));   // no ghost placeholders
    }

    /// <summary>Pumps the UI dispatcher until <paramref name="ready"/> holds or <paramref name="timeout"/>
    /// elapses, so a hung background apply fails the test loudly instead of hanging it.</summary>
    private static async Task Pump(Func<bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(ready(), $"condition did not become true within {timeout}");
    }
}
