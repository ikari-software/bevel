using System.Collections.ObjectModel;
using System.Linq;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The XP-style window grouping model (bevel-m2.10.3): the pure <see cref="TaskbarGrouping.Plan"/>
/// policy and the live <see cref="TaskbarItemsProjector"/> that turns the flat window collection into
/// the grouped strip. No visual tree — this is the model layer.
/// </summary>
public class TaskbarGroupingTests
{
    private static readonly IWindowManager Wm = new GroupStubWm();

    private static TaskItemViewModel Win(string id, string? appId, bool focused = false)
        => new(new ForeignWindow(new ForeignWindowId(id), id, appId, false, focused, default), Wm);

    // ── DisplayName ──
    [Theory]
    [InlineData("com.google.Chrome", "Chrome")]
    [InlineData("com.apple.finder", "Finder")]
    [InlineData("Slack", "Slack")]
    [InlineData(null, "App")]
    [InlineData("", "App")]
    public void DisplayName_takes_the_last_dot_segment_title_cased(string? appId, string expected)
        => Assert.Equal(expected, TaskbarGrouping.DisplayName(appId));

    // ── Plan ──
    [Fact]
    public void Plan_off_passes_every_window_through_in_order()
    {
        var windows = new[] { Win("a", "com.x"), Win("b", "com.x"), Win("c", "com.y") };
        var plan = TaskbarGrouping.Plan(windows, grouping: false);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, e => Assert.False(e.IsGroup));
        Assert.Equal(new[] { "w:a", "w:b", "w:c" }, plan.Select(e => e.Key));
    }

    [Fact]
    public void Plan_on_collapses_an_app_with_two_or_more_windows_into_one_group_at_first_position()
    {
        var windows = new[] { Win("a1", "com.x"), Win("b", "com.y"), Win("a2", "com.x"), Win("a3", "com.x") };
        var plan = TaskbarGrouping.Plan(windows, grouping: true);

        // com.x collapses at a1's slot; com.y (single) stays a normal button.
        Assert.Equal(new[] { "g:com.x", "w:b" }, plan.Select(e => e.Key));
        var group = plan[0];
        Assert.True(group.IsGroup);
        Assert.Equal(3, group.Windows.Count);
        Assert.Equal(new[] { "a1", "a2", "a3" }, group.Windows.Select(w => w.Id.Value));
    }

    [Fact]
    public void Plan_on_leaves_single_window_apps_and_null_app_windows_ungrouped()
    {
        var windows = new[] { Win("a", "com.x"), Win("n1", null), Win("n2", null) };
        var plan = TaskbarGrouping.Plan(windows, grouping: true);
        // Single com.x → button; the two null-app windows never group with each other.
        Assert.All(plan, e => Assert.False(e.IsGroup));
        Assert.Equal(3, plan.Count);
    }

    // ── Projector ──
    [Fact]
    public void Projector_off_mirrors_the_source_one_to_one()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("a", "com.x"), Win("b", "com.x") };
        using var proj = new TaskbarItemsProjector(src, grouping: false);

        Assert.Equal(2, proj.Items.Count);
        Assert.Same(src[0], proj.Items[0]);
        Assert.Same(src[1], proj.Items[1]);
    }

    [Fact]
    public void Projector_on_collapses_and_reacts_to_source_changes()
    {
        var src = new ObservableCollection<TaskItemViewModel>
        {
            Win("c1", "com.chrome"), Win("c2", "com.chrome"), Win("f", "com.finder"),
        };
        using var proj = new TaskbarItemsProjector(src, grouping: true);

        // [ChromeGroup(2), Finder]
        Assert.Equal(2, proj.Items.Count);
        var group = Assert.IsType<TaskGroupViewModel>(proj.Items[0]);
        Assert.Equal(2, group.Count);
        Assert.IsType<TaskItemViewModel>(proj.Items[1]);

        // A second Finder window arrives → Finder now collapses into a group too.
        src.Add(Win("f2", "com.finder"));
        Assert.Equal(2, proj.Items.Count);
        Assert.All(proj.Items, i => Assert.IsType<TaskGroupViewModel>(i));

        // A Chrome window closes down to one → the Chrome group dissolves back to a single button.
        src.Remove(src.First(w => w.Id.Value == "c2"));
        var chrome = proj.Items.OfType<TaskGroupViewModel>().FirstOrDefault(g => g.AppId == "com.chrome");
        Assert.Null(chrome);   // no longer a group
        Assert.Contains(proj.Items, i => i is TaskItemViewModel t && t.Id.Value == "c1");
    }

    [Fact]
    public void Projector_reuses_the_same_group_instance_across_replans()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("c1", "com.chrome"), Win("c2", "com.chrome") };
        using var proj = new TaskbarItemsProjector(src, grouping: true);
        var groupBefore = proj.Items.OfType<TaskGroupViewModel>().Single();

        src.Add(Win("c3", "com.chrome"));   // membership change → replan
        var groupAfter = proj.Items.OfType<TaskGroupViewModel>().Single();

        Assert.Same(groupBefore, groupAfter);   // identity preserved (width/anim/subscriptions survive)
        Assert.Equal(3, groupAfter.Count);
    }

    private sealed class GroupStubWm : IWindowManager
    {
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);
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
