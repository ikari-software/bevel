using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-be86: pinned taskbar apps. A pinned app renders as a taskbar button even with no open
/// window (launch on click); a pin covered by a real window or a running-windowless presence entry
/// never duplicates it; unpinning a running app leaves the window's button in place; duplicate pins
/// are idempotent; the pin set persists through the settings store and re-projects the strip when it
/// changes from outside.
/// </summary>
public class PinnedAppsTests
{
    private sealed class RecordingWm : IWindowManager
    {
        public List<string> Activated { get; } = new();
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default) => Task.CompletedTask;
        public Task HideAppAsync(string bundleId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }

    private sealed class RecordingAppEnv : IAppEnvironment
    {
        public List<string> Launched { get; } = new();
        public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RunningApp>>([]);
        public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<InstalledApp>>([]);
        public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) { Launched.Add(appIdOrPath); return Task.CompletedTask; }
        public event EventHandler<RunningApp>? AppLaunched { add { } remove { } }
        public event EventHandler<RunningApp>? AppTerminated { add { } remove { } }
        public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged { add { } remove { } }
    }

    /// <summary>In-memory <see cref="ISettingsService"/> for VM-level tests: UpdateAsync mutates
    /// Current and raises Changed, like the local store does (the remote path is covered by the
    /// broadcast handler test below).</summary>
    private sealed class StubSettings : ISettingsService
    {
        private readonly BevelSettings _current = TestSettings.NoStacks(new BevelSettings());
        public event Action? Changed;
        public BevelSettings Current => _current;
        public int Version => 0;
        public ThemeOverrides ThemeOverridesFor(string themeId) => new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
        {
            update(_current);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public string SnapshotJson() => "{}";
        public Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default) => Task.CompletedTask;
        public void RaiseChanged() => Changed?.Invoke();
        public void Dispose() { }
    }

    private static TaskItemViewModel Win(string id, string? appId, string? bundleId = null, RecordingWm? wm = null)
        => new(new ForeignWindow(new ForeignWindowId(id), id, appId, false, false, default, BundleId: bundleId), wm ?? new RecordingWm());

    private static TaskItemViewModel Presence(string bundleId, RecordingWm wm)
        => new(new ForeignWindow(new ForeignWindowId("app:" + bundleId), "", bundleId, false, false, default,
            IconPng: null, IsAppPresence: true, BundleId: bundleId), wm);

    private static TaskItemViewModel PinnedVm(string bundleId, Func<string, Task>? launch = null, RecordingWm? wm = null)
        => new(new ForeignWindow(new ForeignWindowId("app:" + bundleId), "App", bundleId, false, false, default,
            IconPng: null, IsAppPresence: true, BundleId: bundleId), wm ?? new RecordingWm(), launchApp: launch);

    private static string[] Ids(TaskbarItemsProjector p) =>
        p.Items.Cast<TaskItemViewModel>().Select(t => t.Id.Value).ToArray();

    // ── Projector (render set) ──────────────────────────────────────────

    [Fact]
    public void Pinned_app_with_no_window_gets_a_button()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("w1", "Other", "com.other") };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.pinned" }, b => PinnedVm(b));

        Assert.Equal(new[] { "w:w1", "app:com.pinned" }, Ids(proj).Select(i => i.StartsWith("app:") ? i : "w:" + i).ToArray());
        var pin = Assert.IsType<TaskItemViewModel>(proj.Items[1]);
        Assert.Equal("app:com.pinned", pin.Id.Value);
        Assert.True(pin.IsAppPresence);
        Assert.Equal("com.pinned", pin.BundleId);
    }

    [Fact]
    public void Pin_while_running_does_not_duplicate_the_window_button()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("w1", "Safari", "com.apple.Safari") };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.apple.Safari" }, b => PinnedVm(b));

        Assert.Equal(new[] { "w1" }, Ids(proj));   // the window's own button covers the pin
    }

    [Fact]
    public void Pin_is_covered_by_a_running_windowless_presence_entry_too()
    {
        var wm = new RecordingWm();
        var src = new ObservableCollection<TaskItemViewModel> { Presence("com.music", wm) };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.music" }, b => PinnedVm(b, wm: wm));

        Assert.Equal(new[] { "app:com.music" }, Ids(proj));   // one button, the helper's presence entry
    }

    [Fact]
    public void Unpin_of_a_running_app_leaves_the_window_button_in_place()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("w1", "Safari", "com.apple.Safari") };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.apple.Safari" }, b => PinnedVm(b));
        proj.SetPinnedApps(Array.Empty<string>(), b => PinnedVm(b));   // unpin

        Assert.Equal(new[] { "w1" }, Ids(proj));   // window open → button stays
    }

    [Fact]
    public void Unpin_of_a_windowless_app_removes_the_button()
    {
        var src = new ObservableCollection<TaskItemViewModel>();
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.pinned" }, b => PinnedVm(b));
        Assert.Single(proj.Items);

        proj.SetPinnedApps(Array.Empty<string>(), b => PinnedVm(b));   // unpin
        Assert.Empty(proj.Items);
    }

    [Fact]
    public void Duplicate_pin_is_idempotent()
    {
        var src = new ObservableCollection<TaskItemViewModel>();
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.pinned", "com.pinned", "com.pinned" }, b => PinnedVm(b));

        Assert.Single(proj.Items);
    }

    [Fact]
    public void Opening_a_window_for_a_pinned_app_covers_the_pin_and_closing_restores_it()
    {
        var src = new ObservableCollection<TaskItemViewModel>();
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.pinned" }, b => PinnedVm(b));
        var pin = Assert.IsType<TaskItemViewModel>(Assert.Single(proj.Items));

        // The app launches → a real window arrives → the synthetic pin is covered (no duplicate).
        src.Add(Win("w1", "Pinned", "com.pinned"));
        Assert.Equal(new[] { "w1" }, Ids(proj));

        // The window closes → the pin's button comes back (same VM instance, identity preserved).
        src.RemoveAt(0);
        Assert.Equal(new[] { "app:com.pinned" }, Ids(proj));
        Assert.Same(pin, proj.Items[0]);
    }

    // ── Click semantics ─────────────────────────────────────────────────

    [Fact]
    public void Clicking_a_pinned_button_launches_the_app_not_activates()
    {
        var wm = new RecordingWm();
        var launched = new List<string>();
        var vm = PinnedVm("com.pinned", b => { launched.Add(b); return Task.CompletedTask; }, wm);

        vm.ActivateCommand.Execute(null);   // recording stubs complete synchronously

        Assert.Equal(new[] { "com.pinned" }, launched);
        Assert.Empty(wm.Activated);   // nothing to activate — the app is not running
    }

    [Fact]
    public void Presence_button_without_a_launch_hook_still_activates()
    {
        // bevel-ww71 regression guard: helper presence entries keep the activate-on-click path.
        var wm = new RecordingWm();
        var vm = Presence("com.music", wm);

        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "app:com.music" }, wm.Activated);
    }

    // ── View-model + settings plumbing ──────────────────────────────────

    [AvaloniaFact]
    public async Task TogglePinAsync_persists_through_settings_and_renders()
    {
        var wm = new RecordingWm();
        var appEnv = new RecordingAppEnv();
        using var model = new ShellModel(wm, appEnv, null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsAppPinned("com.pinned"));
        Assert.Empty(vm.Items);

        await vm.TogglePinAsync("com.pinned");   // pin with no window
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "com.pinned" }, settings.Current.TaskbarPinnedApps);   // persisted
        Assert.True(vm.IsAppPinned("com.pinned"));
        var pin = Assert.IsType<TaskItemViewModel>(Assert.Single(vm.Items));
        Assert.Equal("app:com.pinned", pin.Id.Value);

        // Clicking the pinned button launches via IAppEnvironment (bundle id resolves the .app).
        pin.ActivateCommand.Execute(null);
        Assert.Equal(new[] { "com.pinned" }, appEnv.Launched);

        await vm.TogglePinAsync("com.pinned");   // unpin
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(settings.Current.TaskbarPinnedApps);
        Assert.False(vm.IsAppPinned("com.pinned"));
        Assert.Empty(vm.Items);
    }

    [AvaloniaFact]
    public async Task Duplicate_pin_collapses_and_pin_while_running_does_not_duplicate()
    {
        var wm = new RecordingWm();
        using var model = new ShellModel(wm, new RecordingAppEnv(), null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        Dispatcher.UIThread.RunJobs();

        await vm.TogglePinAsync("com.safari");
        Assert.Equal(new[] { "com.safari" }, settings.Current.TaskbarPinnedApps);
        Assert.Single(vm.Items);

        // A duplicate pin landing in the store (e.g. a racing write from another process) collapses
        // to one entry and one button — pinning is idempotent.
        settings.Current.TaskbarPinnedApps = new[] { "com.safari", "com.safari" };
        settings.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "com.safari" }, vm.PinnedApps);
        Assert.Single(vm.Items);

        // Pin while running: the window's button covers the pin — still exactly one button.
        model.Windows.Add(Win("w1", "Safari", "com.safari", wm));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Items);
        Assert.Equal("w1", Assert.IsType<TaskItemViewModel>(vm.Items[0]).Id.Value);

        // Unpin the running app: the window button stays while the window is open.
        await vm.TogglePinAsync("com.safari");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(settings.Current.TaskbarPinnedApps);
        Assert.Single(vm.Items);
        Assert.Equal("w1", Assert.IsType<TaskItemViewModel>(vm.Items[0]).Id.Value);

        // Window closes: nothing left (the app is not pinned any more, and no presence entry exists).
        model.Windows.RemoveAt(0);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Items);
    }

    [AvaloniaFact]
    public void Pins_arriving_via_a_settings_change_reproject_the_strip()
    {
        // Split mode: another process wrote the pin; the broadcast (Changed) is what applies it here.
        var wm = new RecordingWm();
        using var model = new ShellModel(wm, new RecordingAppEnv(), null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Items);

        settings.Current.TaskbarPinnedApps = new[] { "com.pinned" };
        settings.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsAppPinned("com.pinned"));
        Assert.Equal(new[] { "app:com.pinned" }, vm.Items.Cast<TaskItemViewModel>().Select(t => t.Id.Value).ToArray());
    }

    // ── Render (headless view) ──────────────────────────────────────────

    [AvaloniaFact]
    public void Pinned_app_renders_as_a_real_taskbar_button_with_no_window()
    {
        var wm = new RecordingWm();
        using var model = new ShellModel(wm, new RecordingAppEnv(), null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        settings.Current.TaskbarPinnedApps = new[] { "com.pinned.Fancy" };
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);

        var view = new TaskbarView { DataContext = vm };
        view.Initialize(TestSettings.NoStacks(new BevelSettings()));
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The strip holds exactly the pinned button (no windows exist).
        var pin = Assert.IsType<TaskItemViewModel>(Assert.Single(vm.Items));
        Assert.Equal("app:com.pinned.Fancy", pin.Id.Value);

        // The button template realized for it and rendered the app-name caption.
        var button = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(c => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault())
            .FirstOrDefault(b => b?.DataContext is TaskItemViewModel t && t.Id.Value == "app:com.pinned.Fancy");
        Assert.NotNull(button);
        var caption = button!.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Fancy");
        Assert.NotNull(caption);

        window.Close();
    }
}
