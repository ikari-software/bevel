using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Running-but-windowless app buttons (bevel-ww71): the projector suppresses an app-presence entry
/// whenever a real window for the same app exists (merge transition is duplicate-free), and the VM
/// renders it dim + reopens on click.
/// </summary>
public class AppPresenceTests
{
    private static readonly IWindowManager Wm = new RecordingWm();

    private static TaskItemViewModel Win(string id, string appId)
        => new(new ForeignWindow(new ForeignWindowId(id), id, appId, false, false, default), Wm);

    private static TaskItemViewModel AppPresence(string appId)
        => new(new ForeignWindow(new ForeignWindowId("app:" + appId), "", appId, false, false, default,
            IconPng: null, IsAppPresence: true), Wm);

    [Fact]
    public void Presence_entry_shows_when_the_app_has_no_window()
    {
        var items = new[] { Win("w1", "com.other"), AppPresence("com.music") };
        var plan = TaskbarGrouping.Plan(items, grouping: false);
        Assert.Equal(new[] { "w:w1", "w:app:com.music" }, plan.Select(e => e.Key));
    }

    [Fact]
    public void Presence_entry_is_suppressed_when_a_real_window_for_the_same_app_exists()
    {
        // The merge-transition frame: the app's window has arrived but the presence entry lingers.
        var items = new[] { AppPresence("com.safari"), Win("w1", "com.safari") };
        var plan = TaskbarGrouping.Plan(items, grouping: false);
        Assert.Equal(new[] { "w:w1" }, plan.Select(e => e.Key));   // no duplicate
    }

    [Fact]
    public void Presence_entry_is_suppressed_under_grouping_too()
    {
        var items = new[] { AppPresence("com.x"), Win("a1", "com.x"), Win("a2", "com.x") };
        var plan = TaskbarGrouping.Plan(items, grouping: true);
        Assert.Equal(new[] { "g:com.x" }, plan.Select(e => e.Key));   // grouped, presence gone
    }

    [Fact]
    public void Presence_entry_never_forms_a_group_with_a_real_window()
    {
        // One real window + one presence for the same app must NOT count as a 2-window group.
        var items = new[] { Win("a1", "com.x"), AppPresence("com.x") };
        var plan = TaskbarGrouping.Plan(items, grouping: true);
        Assert.Equal(new[] { "w:a1" }, plan.Select(e => e.Key));   // single window, presence suppressed
    }

    [Fact]
    public void Presence_button_is_dim()
    {
        Assert.Equal(0.6, AppPresence("com.music").ContentOpacity);
        Assert.Equal(1.0, Win("w1", "com.other").ContentOpacity);
    }

    [Fact]
    public void Presence_button_label_is_italic()
    {
        Assert.Equal(Avalonia.Media.FontStyle.Italic, AppPresence("com.music").TitleStyle);
        Assert.Equal(Avalonia.Media.FontStyle.Normal, Win("w1", "com.other").TitleStyle);
    }

    [Fact]
    public void Sort_orders_the_strip_by_name_and_windowless_last()
    {
        var source = new ObservableCollection<TaskItemViewModel>
        {
            Win("z", "Zebra"),
            AppPresence("Aardvark"),   // windowless — alphabetically first, but should land LAST when enabled
            Win("a", "Apple"),
        };
        using var proj = new TaskbarItemsProjector(source, TaskbarGroupingMode.Never);

        // Open order + windowless-last: windows keep their source order, the dock-dot trails.
        proj.SetSort(TaskbarWindowSort.OpenOrder, windowlessLast: true);
        Assert.Equal(new[] { "Zebra", "Apple", "Aardvark" }, Names(proj));

        // By name + windowless-last: windows A→Z, the dock-dot still trails.
        proj.SetSort(TaskbarWindowSort.Name, windowlessLast: true);
        Assert.Equal(new[] { "Apple", "Zebra", "Aardvark" }, Names(proj));

        // By name, windowless mixed in: pure A→Z, so the dock-dot sorts by its own name.
        proj.SetSort(TaskbarWindowSort.Name, windowlessLast: false);
        Assert.Equal(new[] { "Aardvark", "Apple", "Zebra" }, Names(proj));

        static string[] Names(TaskbarItemsProjector p)
            => p.Items.Cast<TaskItemViewModel>().Select(t => t.AppId!).ToArray();
    }

    [Fact]
    public void Colour_sort_maps_rgb_to_hue()
    {
        Assert.Equal(0, TaskbarItemsProjector.RgbToHue(1, 0, 0), 1);     // red
        Assert.Equal(60, TaskbarItemsProjector.RgbToHue(1, 1, 0), 1);    // yellow
        Assert.Equal(120, TaskbarItemsProjector.RgbToHue(0, 1, 0), 1);   // green
        Assert.Equal(240, TaskbarItemsProjector.RgbToHue(0, 0, 1), 1);   // blue
        Assert.Equal(0, TaskbarItemsProjector.RgbToHue(0.5, 0.5, 0.5), 1); // grey → 0
    }

    [Fact]
    public void Clicking_a_presence_button_reopens_via_activate()
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("app:com.music"), "", "com.music", false, false, default,
                IconPng: null, IsAppPresence: true), wm);

        // RecordingWm returns completed tasks, so the async toggle runs to completion synchronously here.
        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "app:com.music" }, wm.Activated);
        Assert.Empty(wm.Minimized);   // no minimize/toggle for a windowless app
    }

    [Fact]
    public void Quit_terminates_the_app_by_bundle_id_gracefully()
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Doc", "Safari", false, false, default,
                BundleId: "com.apple.Safari"), wm);

        vm.QuitCommand.Execute(null);
        Assert.Equal(new[] { "com.apple.Safari:false" }, wm.Terminated);
    }

    [Fact]
    public void Force_quit_sets_the_force_flag()
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Doc", "Safari", false, false, default,
                BundleId: "com.apple.Safari"), wm);

        vm.ForceQuitCommand.Execute(null);
        Assert.Equal(new[] { "com.apple.Safari:true" }, wm.Terminated);
    }

    [Fact]
    public void Presence_bundle_id_is_parsed_from_the_app_prefixed_id()
    {
        // An app-presence entry with no explicit BundleId still quits — the id is "app:<bundle>".
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("app:com.apple.Music"), "", "Music", false, false, default,
                IconPng: null, IsAppPresence: true), wm);

        vm.QuitCommand.Execute(null);
        Assert.Equal(new[] { "com.apple.Music:false" }, wm.Terminated);
    }

    private sealed class RecordingWm : IWindowManager
    {
        public List<string> Activated { get; } = new();
        public List<string> Minimized { get; } = new();
        public List<string> Terminated { get; } = new();
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) { Minimized.Add(id.Value); return Task.CompletedTask; }
        public Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default) { Terminated.Add($"{bundleId}:{force.ToString().ToLowerInvariant()}"); return Task.CompletedTask; }
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
