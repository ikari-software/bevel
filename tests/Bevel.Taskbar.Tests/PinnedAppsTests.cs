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
        public List<InstalledApp> Installed { get; } = new();
        public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RunningApp>>([]);
        public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Installed);
        public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) { Launched.Add(appIdOrPath); return Task.CompletedTask; }
        public event EventHandler<RunningApp>? AppLaunched { add { } remove { } }
        public event EventHandler<RunningApp>? AppTerminated { add { } remove { } }
        public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
        public void RaiseInstalledAppsChanged() => InstalledAppsChanged?.Invoke(this, Installed);
    }

    /// <summary>Counts icon renders; yields a null bitmap (width 0) — only the CALL count matters.</summary>
    private sealed class CountingIconProvider : IIconProvider
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task WhenCalled => _called.Task;
        public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            _called.TrySetResult();
            return ValueTask.FromResult(new PalImage(0, 0, Array.Empty<byte>()));
        }
        public event EventHandler? IconInvalidated { add { } remove { } }
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

    // ── bevel-be86 review fixes ─────────────────────────────────────────

    [AvaloniaFact]
    public void Path_shaped_pin_is_refused_at_the_launch_sink_and_app_prefix_is_stripped()
    {
        var wm = new RecordingWm();
        var appEnv = new RecordingAppEnv();
        using var model = new ShellModel(wm, appEnv, null, usage: TestUsage.Scratch());

        // A path-shaped key never reaches IAppEnvironment.LaunchAsync (bevel-318a invariant).
        model.CreatePinnedItem("/Applications/Evil.app").ActivateCommand.Execute(null);
        model.CreatePinnedItem("~/evil").ActivateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(appEnv.Launched);

        // A valid bundle id launches; a leading "app:" prefix is stripped at the sink too.
        model.CreatePinnedItem("com.valid").ActivateCommand.Execute(null);
        model.CreatePinnedItem("app:com.prefixed").ActivateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "com.valid", "com.prefixed" }, appEnv.Launched);
    }

    [AvaloniaFact]
    public async Task TogglePinAsync_strips_the_app_prefix_and_refuses_path_shaped_ids()
    {
        var wm = new RecordingWm();
        using var model = new ShellModel(wm, new RecordingAppEnv(), null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        Dispatcher.UIThread.RunJobs();

        await vm.TogglePinAsync("app:com.foo");   // keyed on the bare bundle id
        Assert.Equal(new[] { "com.foo" }, settings.Current.TaskbarPinnedApps);
        Assert.True(vm.IsAppPinned("app:com.foo"));
        Assert.True(vm.IsAppPinned("com.foo"));

        // A path-shaped id is refused outright — never persisted, never launched.
        await vm.TogglePinAsync("/Applications/Evil.app");
        await vm.TogglePinAsync("evil.app");
        Assert.Equal(new[] { "com.foo" }, settings.Current.TaskbarPinnedApps);
        Assert.False(vm.IsAppPinned("/Applications/Evil.app"));
    }

    [AvaloniaFact]
    public void Pins_applied_from_settings_are_sanitized_and_capped()
    {
        var wm = new RecordingWm();
        using var model = new ShellModel(wm, new RecordingAppEnv(), null, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        Dispatcher.UIThread.RunJobs();

        var pins = new[] { "app:com.prefixed", "/evil.app", new string('x', 600) }
            .Concat(Enumerable.Range(0, 200).Select(i => $"com.app{i:D3}"))
            .ToArray();
        settings.Current.TaskbarPinnedApps = pins;
        settings.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TaskbarPins.MaxCount, vm.PinnedApps.Count);
        Assert.Equal("com.prefixed", vm.PinnedApps[0]);   // "app:" prefix stripped
        Assert.Equal("com.app126", vm.PinnedApps[^1]);     // capped at 128
        Assert.DoesNotContain(vm.PinnedApps, p => p.Length > TaskbarPins.MaxIdLength || p.Contains('/'));
    }

    [Fact]
    public void Covered_set_uses_visible_items_so_an_other_space_window_does_not_cover_its_pin()
    {
        var otherSpace = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Safari", "Safari", false, false, default,
                BundleId: "com.safari", IsOnOtherSpace: true), new RecordingWm());
        var src = new ObservableCollection<TaskItemViewModel> { otherSpace };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.safari" }, b => PinnedVm(b));

        // Visible (other Spaces shown): the window covers the pin — no duplicate.
        Assert.Equal(new[] { "w1" }, Ids(proj));

        // Other-Spaces filter ON: the window leaves the strip, so it must NOT cover the pin —
        // the pin's own synthetic button renders instead.
        proj.SetShowOtherSpaces(false);
        Assert.Equal(new[] { "app:com.safari" }, Ids(proj));
    }

    [Fact]
    public void BundleId_learned_late_heals_the_duplicate_pin_button()
    {
        var wm = new RecordingWm();
        var win = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Safari", "Safari", false, false, default,
                BundleId: null), wm);
        var src = new ObservableCollection<TaskItemViewModel> { win };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.safari" }, b => PinnedVm(b, wm: wm));

        // No bundle id yet (AppId "Safari" ≠ pin key) → the pin renders its own button alongside.
        Assert.Equal(2, proj.Items.Count);

        // The platform attaches the bundle id → Update re-keys the VM in place → the projector
        // re-plans on the BundleId change → the window now covers the pin, duplicate heals.
        win.Update(new ForeignWindow(new ForeignWindowId("w1"), "Safari", "Safari", false, false, default,
            BundleId: "com.safari"));
        Assert.Equal(new[] { "w1" }, Ids(proj));
    }

    [Fact]
    public void A_closing_window_still_covers_its_pin_until_removal_commits()
    {
        var win = Win("w1", "Safari", "com.safari");
        win.IsClosing = true;   // exit animation in flight, still in the source
        var src = new ObservableCollection<TaskItemViewModel> { win };
        using var proj = new TaskbarItemsProjector(src);
        proj.SetPinnedApps(new[] { "com.safari" }, b => PinnedVm(b));

        Assert.Equal(new[] { "w1" }, Ids(proj));   // no synthetic pin button while closing

        src.RemoveAt(0);   // removal commits → the pin's button comes back
        Assert.Equal(new[] { "app:com.safari" }, Ids(proj));
    }

    [AvaloniaFact]
    public async Task Pinned_icon_is_retried_on_InstalledAppsChanged_and_matched_by_exact_id_only()
    {
        var wm = new RecordingWm();
        var appEnv = new RecordingAppEnv();
        var icons = new CountingIconProvider();
        using var model = new ShellModel(wm, appEnv, icons, usage: TestUsage.Scratch());
        var settings = new StubSettings();
        settings.Current.TaskbarPinnedApps = new[] { "com.late" };   // pin "com.late" → display name "Late"
        using var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);
        model.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Volatile.Read(ref icons.Calls));   // no installed apps yet → no icon path

        // SPOOF attempt: an installed app whose DISPLAY NAME matches the pin's derived name ("Late")
        // but whose id is a bundle PATH — the old display-name fallback would render ITS icon for the
        // pin. Exact-id-only matching must refuse it.
        appEnv.Installed.Add(new InstalledApp("/Applications/Safari.app", "Late", "/Applications/Safari.app"));
        appEnv.RaiseInstalledAppsChanged();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(150);   // let any (wrong) off-thread icon load land
        Assert.Equal(0, Volatile.Read(ref icons.Calls));

        // The real entry lands (an app installed after startup) → InstalledAppsChanged → the pinned
        // icon load is retried and now resolves by EXACT id match.
        appEnv.Installed.Add(new InstalledApp("com.late", "Late", "/Applications/Late.app"));
        appEnv.RaiseInstalledAppsChanged();
        Dispatcher.UIThread.RunJobs();
        await icons.WhenCalled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref icons.Calls) >= 1);
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
