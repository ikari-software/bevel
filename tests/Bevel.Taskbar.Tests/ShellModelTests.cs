using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

public sealed class ShellModelTests
{
    [AvaloniaFact]
    public async Task Programs_reconcile_live_when_installed_apps_change()
    {
        // The Start menu's Programs must NOT be a one-shot startup list: installing or removing an app
        // updates it live via IAppEnvironment.InstalledAppsChanged (bevel).
        var appEnv = new StubAppEnvironment(new InstalledApp("com.a", "Alpha", null));
        using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start();
        for (var i = 0; i < 50 && model.Programs.Count < 1; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Equal("Alpha", Assert.Single(model.Programs).DisplayName);

        // App installed → the list grows live, in place.
        appEnv.RaiseInstalledAppsChanged(new[]
        {
            new InstalledApp("com.a", "Alpha", null),
            new InstalledApp("com.b", "Beta", null),
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, model.Programs.Count);
        Assert.Equal("Beta", model.Programs[1].DisplayName);

        // App removed → it drops live.
        appEnv.RaiseInstalledAppsChanged(new[] { new InstalledApp("com.b", "Beta", null) });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Beta", Assert.Single(model.Programs).DisplayName);
    }

    [AvaloniaFact]
    public async Task Reappearing_window_is_revived_in_place()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var original = Window("w1", "Old title");
        manager.Live = [original];
        model.Start();
        manager.RaiseOpened(original);
        Dispatcher.UIThread.RunJobs();

        var item = Assert.Single(model.Windows);
        item.Width = 120;
        item.Opacity = 1;

        manager.RaiseClosed(original);
        Dispatcher.UIThread.RunJobs();
        Assert.True(item.IsClosing);

        var renamed = Window("w1", "New title");
        manager.Live = [renamed];
        manager.RaiseOpened(renamed);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(item, Assert.Single(model.Windows));
        Assert.Equal("New title", item.Title);
        Assert.False(item.IsClosing);
        Assert.Equal(120, item.Width);
        Assert.Equal(1, item.Opacity);

        // The stale exit timer from the transient close must not remove the revived item.
        await Task.Delay(220);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(item, Assert.Single(model.Windows));
    }

    [AvaloniaFact]
    public async Task Closed_window_is_removed_after_exit_animation()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var window = Window("w1", "Title");
        manager.Live = [window];
        model.Start();
        manager.RaiseOpened(window);
        Dispatcher.UIThread.RunJobs();

        manager.Live = [];
        manager.RaiseClosed(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(model.Windows);

        await Task.Delay(220);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(model.Windows);
    }

    [AvaloniaFact]
    public void Foreground_change_makes_focus_exclusive()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var first = Window("w1", "First", focused: true);
        var second = Window("w2", "Second");
        manager.Live = [first, second];
        model.Start();
        manager.RaiseOpened(first);
        manager.RaiseOpened(second);
        Dispatcher.UIThread.RunJobs();

        manager.RaiseForeground(Window("w2", "Second", focused: true));
        Dispatcher.UIThread.RunJobs();

        Assert.False(model.Windows.Single(w => w.Id.Value == "w1").IsFocused);
        Assert.True(model.Windows.Single(w => w.Id.Value == "w2").IsFocused);
    }

    [AvaloniaFact]
    public void Stale_focus_on_changed_event_does_not_press_multiple_buttons()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var first = Window("w1", "First", focused: true);
        var second = Window("w2", "Second");
        manager.Live = [first, second];
        model.Start();
        manager.RaiseOpened(first);
        manager.RaiseOpened(second);
        manager.RaiseForeground(Window("w1", "First", focused: true));
        Dispatcher.UIThread.RunJobs();

        // A title/minimize refresh that still carries a stale isFocused=true must not
        // press a second button — focus stays on w1 until ForegroundChanged / reconcile.
        manager.RaiseChanged(Window("w2", "Second", focused: true));
        Dispatcher.UIThread.RunJobs();

        Assert.True(model.Windows.Single(w => w.Id.Value == "w1").IsFocused);
        Assert.False(model.Windows.Single(w => w.Id.Value == "w2").IsFocused);
    }

    [AvaloniaFact]
    public void Unfocused_foreground_event_does_not_claim_pressed_state()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var first = Window("w1", "First", focused: true);
        var second = Window("w2", "Second");
        manager.Live = [first, second];
        model.Start();
        manager.RaiseOpened(first);
        manager.RaiseOpened(second);
        manager.RaiseForeground(Window("w1", "First", focused: true));
        Dispatcher.UIThread.RunJobs();

        manager.RaiseForeground(Window("w2", "Second", focused: false));
        Dispatcher.UIThread.RunJobs();

        Assert.True(model.Windows.Single(w => w.Id.Value == "w1").IsFocused);
        Assert.False(model.Windows.Single(w => w.Id.Value == "w2").IsFocused);
    }

    [AvaloniaFact]
    public async Task Reconcile_with_no_focused_window_keeps_sticky_pressed_state()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        var first = Window("w1", "First", focused: true);
        var second = Window("w2", "Second");
        manager.Live = [first, second];
        model.Start();
        manager.RaiseOpened(first);
        manager.RaiseOpened(second);
        manager.RaiseForeground(Window("w1", "First", focused: true));
        Dispatcher.UIThread.RunJobs();

        manager.Live = [Window("w1", "First"), Window("w2", "Second")];
        await Task.Delay(2100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(model.Windows.Single(w => w.Id.Value == "w1").IsFocused);
        Assert.False(model.Windows.Single(w => w.Id.Value == "w2").IsFocused);
    }

    [Fact]
    public void Detached_macos_restart_wraps_nohup()
    {
        var inner = Bevel.App.Program.CreateRestartStartInfo(
            "/usr/local/bin/dotnet",
            "/tmp/Bevel.App.dll",
            ["--pal", "macos"]);
        inner.WorkingDirectory = "/tmp";
        var detached = Bevel.App.Program.CreateDetachedMacOSStartInfo(inner);

        Assert.Equal("/bin/sh", detached.FileName);
        Assert.Equal("-c", detached.ArgumentList[0]);
        Assert.Contains("bevel-restart.log", detached.ArgumentList[1]);
        Assert.Contains("nohup '/usr/local/bin/dotnet' '/tmp/Bevel.App.dll' '--pal' 'macos'", detached.ArgumentList[1]);
        Assert.Equal("/tmp", detached.WorkingDirectory);
    }

    [Fact]
    public void Click_action_matches_native_window_state()
    {
        var manager = new StubWindowManager();

        // StatusText/ContentOpacity describe the click AFFORDANCE for the current state, so assert them
        // before the click. The click then optimistically flips the state (bevel-nxic), so post-click
        // StatusText intentionally reflects the new state, not the old one.
        var minimized = new TaskItemViewModel(Window("min", "Minimized", minimized: true), manager);
        Assert.Contains("click to restore", minimized.StatusText);
        Assert.Equal(0.55, minimized.ContentOpacity);
        minimized.ActivateCommand.Execute(null);
        Assert.Equal(["restore:min", "activate:min"], manager.Actions);
        Assert.False(minimized.IsMinimized);   // optimistic: un-minimized on the click

        manager.Actions.Clear();
        var active = new TaskItemViewModel(Window("active", "Active", focused: true), manager);
        Assert.Contains("click to minimize", active.StatusText);
        active.ActivateCommand.Execute(null);
        Assert.Equal(["minimize:active"], manager.Actions);
        Assert.True(active.IsMinimized);       // optimistic: minimized on the click

        manager.Actions.Clear();
        var inactive = new TaskItemViewModel(Window("open", "Open"), manager);
        Assert.Contains("click to activate", inactive.StatusText);
        inactive.ActivateCommand.Execute(null);
        Assert.Equal(["activate:open"], manager.Actions);
    }

    private static ForeignWindow Window(
        string id,
        string title,
        bool minimized = false,
        bool focused = false) =>
        new(new ForeignWindowId(id), title, "App", minimized, focused, default);

    /// <summary>A small, real, decodable PNG (WriteableBitmap.Save uses the Skia encoder) so the icon
    /// decode path in <c>LoadWindowIcon</c> runs for real.</summary>
    private static byte[] IconPng()
    {
        var wb = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }

    [AvaloniaFact]
    public async Task Window_seen_before_its_icon_is_ready_picks_it_up_on_a_later_snapshot()
    {
        // Regression: the taskbar "sometimes loses a window's icon". A window first enumerated before
        // its app icon is ready arrives with IconPng == null, so its button is created icon-less. Nothing
        // re-attempted the load, so it stayed blank for the window's whole life. ApplyUpdate now retries
        // while IconSource is null — and must NOT blank a good icon when a later snapshot lacks one.
        var png = IconPng();
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());

        var iconless = new ForeignWindow(new ForeignWindowId("w1"), "Doc", "App", false, false, default, IconPng: null);
        manager.Live = [iconless];
        model.Start();
        manager.RaiseOpened(iconless);
        Dispatcher.UIThread.RunJobs();

        var item = Assert.Single(model.Windows);
        Assert.Null(item.IconSource); // created blank — the icon wasn't ready that cycle

        // A later snapshot carries the icon → the button must pick it up (was: blank forever).
        var withIcon = new ForeignWindow(new ForeignWindowId("w1"), "Doc", "App", false, false, default, IconPng: png);
        manager.Live = [withIcon];
        manager.RaiseChanged(withIcon);
        for (var i = 0; i < 100 && item.IconSource is null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.NotNull(item.IconSource);

        // Safety half: a subsequent icon-less snapshot must NOT blank the good icon.
        var loaded = item.IconSource;
        manager.RaiseChanged(new ForeignWindow(new ForeignWindowId("w1"), "Doc", "App", false, false, default, IconPng: null));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(loaded, item.IconSource);
    }

    [AvaloniaFact]
    public async Task MinimizeAll_minimizes_every_tracked_window()
    {
        var manager = new StubWindowManager();
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        model.Windows.Add(new TaskItemViewModel(Window("a", "A"), manager));
        model.Windows.Add(new TaskItemViewModel(Window("b", "B"), manager));

        await model.MinimizeAllAsync();

        Assert.Contains("minimize:a", manager.Actions);
        Assert.Contains("minimize:b", manager.Actions);
    }

    [AvaloniaFact]
    public async Task MinimizeAll_keeps_going_when_one_window_throws()
    {
        var manager = new StubWindowManager { ThrowOnMinimizeId = "b" };
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());
        model.Windows.Add(new TaskItemViewModel(Window("a", "A"), manager));
        model.Windows.Add(new TaskItemViewModel(Window("b", "B"), manager));
        model.Windows.Add(new TaskItemViewModel(Window("c", "C"), manager));

        await model.MinimizeAllAsync();   // must not throw despite 'b' failing

        Assert.Contains("minimize:a", manager.Actions);
        Assert.Contains("minimize:c", manager.Actions);   // loop continued past the failure
        Assert.DoesNotContain("minimize:b", manager.Actions);
    }

    [AvaloniaFact]
    public async Task Capture_is_bounded_and_falls_back_without_blocking()
    {
        // bevel-1275: the window-state-change / hover-thumbnail path must never hang on a cold or wedged
        // ScreenCaptureKit capture. A capture that never completes on its own must still resolve promptly
        // once the caller's deadline fires (the real MacOSWindowManager now also carries an intrinsic
        // deadline so a caller that forgets is bounded too) — returning null so the button keeps its
        // static app icon, never wedging the pipeline.
        var manager = new StubWindowManager { CaptureHangs = true };
        using var model = new ShellModel(manager, null, null, usage: TestUsage.Scratch());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var png = await model.CaptureWindowAsync(new ForeignWindowId("w1"), 0, 0, cts.Token);
        sw.Stop();

        Assert.Null(png);                                  // bounded → fall back to the app icon, no preview
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),  // resolved on the deadline, did not block indefinitely
            $"capture should be bounded by the caller deadline, took {sw.ElapsedMilliseconds}ms");
    }

    private sealed class StubWindowManager : IWindowManager
    {
        public IReadOnlyList<ForeignWindow> Live { get; set; } = [];
        public List<string> Actions { get; } = [];

        public Capabilities Capabilities { get; } =
            new(true, TrayCapability.Mirrored, []);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Live);

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default)
        {
            Actions.Add($"activate:{id.Value}");
            return Task.CompletedTask;
        }

        public string? ThrowOnMinimizeId { get; set; }

        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default)
        {
            if (id.Value == ThrowOnMinimizeId)
                throw new InvalidOperationException("stub minimize failure");
            Actions.Add($"minimize:{id.Value}");
            return Task.CompletedTask;
        }

        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default)
        {
            Actions.Add($"restore:{id.Value}");
            return Task.CompletedTask;
        }
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>When set, a capture blocks until its token cancels — a stand-in for a wedged/cold
        /// ScreenCaptureKit init in the helper — then returns null (the PAL's bounded-timeout fallback to
        /// the static app icon). Lets a test assert the capture path is bounded and never blocks (bevel-1275).</summary>
        public bool CaptureHangs { get; set; }

        public async Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
        {
            if (!CaptureHangs) return null;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return null;   // deadline fired → no preview, mirrors MacOSWindowManager's null fallback
        }

        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;

        public void RaiseOpened(ForeignWindow window) => WindowOpened?.Invoke(this, window);
        public void RaiseClosed(ForeignWindow window) => WindowClosed?.Invoke(this, window);
        public void RaiseChanged(ForeignWindow window) => WindowChanged?.Invoke(this, window);
        public void RaiseForeground(ForeignWindow window) => ForegroundChanged?.Invoke(this, window);
    }

    [AvaloniaFact]
    public async Task Programs_reconcile_from_installed_apps()
    {
        var appEnv = new StubAppEnvironment(
            new InstalledApp("com.a", "Alpha", null),
            new InstalledApp("com.b", "Beta", null));
        using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start(); // LoadProgramsAsync enumerates off-thread, then reconciles on the UI thread

        for (var i = 0; i < 50 && model.Programs.Count < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(["Alpha", "Beta"], model.Programs.Select(p => p.DisplayName));
    }
}
