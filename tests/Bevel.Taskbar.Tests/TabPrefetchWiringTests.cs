using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The hover prefetch must be WIRED, not merely implemented (bevel-1fy8).
///
/// <para><see cref="TabPrefetchTests"/> proves the cache reuses and expires correctly, but a correct
/// component that nothing calls buys nothing — that is exactly how bevel-3gad's icon pool shipped with
/// no writer for weeks while its own tests passed. This drives the real
/// <c>TaskbarView.OnTaskButtonPointerEntered</c> on a realized task button and asserts the tab provider
/// was actually queried, so the connection between the pointer and the fetch is covered.</para>
/// </summary>
public sealed class TabPrefetchWiringTests
{
    private const string Browser = "com.example.browser";

    private sealed class RecordingTabs : ITabProvider
    {
        public readonly List<string> Queried = [];
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public bool SupportsApp(string? bundleId) => bundleId == Browser;

        public ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
        {
            lock (Queried) Queried.Add(bundleId);
            return ValueTask.FromResult<IReadOnlyList<AppTab>>(new[] { new AppTab(bundleId, "1", 1, "Tab 1") });
        }

        public Task<TabActivationResult> ActivateAsync(AppTab tab, CancellationToken ct = default)
            => Task.FromResult(TabActivationResult.Activated);
    }

    private static (TaskbarView View, TaskbarWindow Window, ShellModel Model, RecordingTabs Tabs) Realize(string appId)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var tabs = new RecordingTabs();
        view.Initialize(TestSettings.NoStacks(new Bevel.Core.BevelSettings()), tabProvider: tabs);
        var window = new TaskbarWindow(null) { Content = view, Width = 800, Height = 40 };

        // AppId is the FRIENDLY NAME; BundleId is the machine key the tab provider is gated on.
        var fw = new ForeignWindow(new ForeignWindowId("w1"), "A page", "App", false, false, default,
            BundleId: appId);
        model.Windows.Add(new TaskItemViewModel(fw, new NoOpWindowManager()) { Width = 120, Opacity = 1 });

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window, model, tabs);
    }

    private static void Hover(ToggleButton button) =>
        button.RaiseEvent(new PointerEventArgs(
            InputElement.PointerEnteredEvent, button, new Pointer(1, PointerType.Mouse, isPrimary: true),
            button, default, 0, new PointerPointProperties(), KeyModifiers.None));

    /// <summary>Hovering a tab-capable app's button starts the enumeration — the whole point, since it
    /// is the only moment before the right-click where the osascript cost can be spent.</summary>
    [AvaloniaFact]
    public void Hovering_a_tab_capable_button_starts_the_fetch()
    {
        var (view, window, _, tabs) = Realize(Browser);
        try
        {
            var button = view.WindowButtonAreaControl.GetVisualDescendants().OfType<ToggleButton>().Single();
            Hover(button);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(Browser, tabs.Queried);
        }
        finally { window.Close(); }
    }

    /// <summary>...and hovering anything else does not. Hover reaches EVERY task button, so without the
    /// provider's own gate this would fire an osascript (and on macOS an Automation consent prompt) at
    /// every app the pointer crosses on its way across the strip.</summary>
    [AvaloniaFact]
    public void Hovering_an_app_with_no_tab_dialect_queries_nothing()
    {
        var (view, window, _, tabs) = Realize("com.apple.TextEdit");
        try
        {
            var button = view.WindowButtonAreaControl.GetVisualDescendants().OfType<ToggleButton>().Single();
            Hover(button);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(tabs.Queried);
        }
        finally { window.Close(); }
    }

    /// <summary>Hovering the same button repeatedly (the pointer re-enters as it crosses the strip) must
    /// not fire a query per crossing.</summary>
    [AvaloniaFact]
    public void Repeated_hovers_do_not_re_query()
    {
        var (view, window, _, tabs) = Realize(Browser);
        try
        {
            var button = view.WindowButtonAreaControl.GetVisualDescendants().OfType<ToggleButton>().Single();
            for (var i = 0; i < 5; i++)
            {
                Hover(button);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Single(tabs.Queried);
        }
        finally { window.Close(); }
    }

    private sealed class NoOpWindowManager : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int w, int h, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }
}
