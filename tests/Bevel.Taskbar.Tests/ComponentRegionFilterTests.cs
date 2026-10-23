using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 14 fix round 2, Finding 1 (as revised by the whole-branch review's Fix 1): proves
/// <see cref="TaskbarView.ApplyComponentRegion"/> ITSELF — the real production call site, not a
/// test's own stand-in filter — keeps the stacks region from regressing when handed the FULL
/// migrated component list (Start, WindowStrip, Stack, Tray, Clock, ShowDesktop).
///
/// Before the review, this asserted that the filter let Stack slots through while blocking every
/// other type. The review found that the "chosen remedy" for the ghost-placeholder/generic-button
/// regression is NOT a renderer: the stacks region was restored to its pre-component-list
/// hand-built <c>ItemsControl</c> (bound to <c>Stacks.Stacks</c>, in <c>TaskbarView.axaml</c>), and
/// <c>ApplyComponentRegion</c>'s filter now composes NOTHING — no component type has a renderer yet,
/// so handing over even a single resolved type would render a generic captioned button in a region
/// a shipped feature already owns. This test now asserts that empty result, still driven through
/// the real method via the <c>InternalsVisibleTo</c> seam <c>Bevel.Taskbar.csproj</c> already grants
/// this assembly (the same one <c>WorkAreaMitigator</c> and <c>RemoteComponentChannel.OnBusMessage</c>
/// rely on) — not a direct <see cref="ComponentBarHost.ApplyAsync"/> call, which would pass even if
/// the production filter were deleted entirely.
/// </summary>
[Collection("TaskbarTheme")]
public class ComponentRegionFilterTests
{
    [AvaloniaFact]
    public async Task ApplyComponentRegion_composes_no_slots_from_a_full_migrated_list()
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
        // Clock and ShowDesktop.
        var full = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        Assert.True(full.Select(i => i.TypeId).Distinct().Count() > 1,
            "the default migrated list should contain more than one component type");

        view.ApplyComponentRegion(new BevelSettings { TaskbarComponents = full });

        // ApplyComponentRegion fires ApplyAsync via Task.Run and returns immediately; pump the
        // dispatcher for a bounded grace period so the background run's UI-thread hop (if any) has
        // every chance to land before the final assertion.
        await PumpFor(TimeSpan.FromMilliseconds(500));

        Assert.Empty(host!.Slots);
    }

    /// <summary>Pumps the UI dispatcher for a fixed grace period, so a background apply that WOULD
    /// mutate <c>Slots</c> has every opportunity to do so before the assertion runs.</summary>
    private static async Task PumpFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
