using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Core.Components;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Keyboard-accessibility coverage for the taskbar (bevel-vk4n). The taskbar was entirely
/// keyboard-unreachable: every control was <c>Focusable=False</c> and the window never became key.
/// These tests pin the fix — the key controls are focusable and carry an accessible name, the Start
/// menu takes focus into itself on open, and the window's become-key scope is false at idle and true
/// only while a menu it owns is open (the menu-scoped key-focus model).
///
/// The taskbar UI can't be driven by synthetic OS clicks, so these exercise the real visual tree under
/// the headless Skia backend and assert <see cref="AutomationProperties"/> / <see cref="InputElement.Focusable"/>
/// plus the managed become-key flag directly.
/// </summary>
[Collection("TaskbarTheme")]
public class TaskbarAccessibilityTests
{
    // ── (a) Key controls are focusable and named ─────────────────────────

    [AvaloniaFact]
    public void Start_button_is_focusable_with_an_accessible_name()
    {
        using var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = view.StartButtonControl;
        Assert.True(start.Focusable);

        // The name must never be empty — it is all a screen reader has to announce. It tracks the
        // configured caption (WCAG 2.5.3 Label in Name: the accessible name contains the visible
        // label), and falls back to the button's FUNCTION when the caption is logo-only, which is
        // exactly when there is no visible label to borrow.
        Assert.Equal(BevelSettings.DefaultStartLabel, AutomationProperties.GetName(start));

        view.ApplyLiveSettings(new BevelSettings { TaskbarStartLabel = "Go!" });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Go!", AutomationProperties.GetName(start));

        view.ApplyLiveSettings(new BevelSettings { TaskbarStartLabel = "" });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Start menu", AutomationProperties.GetName(start));
    }

    [AvaloniaFact]
    public void A_task_button_is_focusable_with_its_window_title_as_the_accessible_name()
    {
        using var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new NoopWindowManager();
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fw = new ForeignWindow(new ForeignWindowId("w0"), "My Window", "App", false, true, default);
        model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        Dispatcher.UIThread.RunJobs();

        var button = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(c => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault())
            .FirstOrDefault(b => b is not null);
        Assert.NotNull(button);
        Assert.True(button!.Focusable);
        Assert.Equal("My Window", AutomationProperties.GetName(button));
    }

    [AvaloniaFact]
    public void A_tray_icon_is_focusable_with_its_tooltip_as_the_accessible_name()
    {
        using var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var host = new OneItemTray(new TrayItem(new TrayItemId("1:10"), "Wi-Fi"));
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), tray: host);
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The focusable, automation-named element is the CELL (icon-or-placeholder, bevel-yduf), not
        // the Image inside it — so an item that has no icon yet is reachable exactly like one that has.
        var cell = window.GetVisualDescendants().OfType<TrayIconCell>()
            .FirstOrDefault(c => c.DataContext is TrayItemViewModel);
        Assert.NotNull(cell);
        Assert.True(cell!.Focusable);
        Assert.Equal("Wi-Fi", AutomationProperties.GetName(cell));
    }

    // ── (b) The Start menu takes focus into itself on open ───────────────

    [AvaloniaFact]
    public async Task Opening_the_start_menu_focuses_an_element_inside_it()
    {
        var placement = new Button();
        var menu = new StartMenu();
        var panel = new StackPanel { Children = { placement, menu } };
        var window = new Window { Content = panel, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await menu.OpenAsync(placement);
        Dispatcher.UIThread.RunJobs();

        // The Win2000 layout is active by default; its first row is the Programs cascade. OpenAsync must
        // leave focus on it so arrow keys work immediately (avalonia-popup-needs-visual-tree gotcha).
        var programs = menu.FindControl<MenuItem>("ProgramsItem");
        Assert.NotNull(programs);
        Assert.True(programs!.IsFocused, "the Start menu should focus its first item on open");
    }

    // ── (c) Become-key scope: false at idle, true only while a menu is open ─

    [AvaloniaFact]
    public void Taskbar_become_key_is_false_at_idle_and_true_only_while_a_menu_is_open()
    {
        using var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Idle: the taskbar must not be allowed to steal key focus.
        Assert.False(window.IsKeyFocusAllowed);

        // Open the Start menu via the real click path — the popup's Opened drives the menu scope.
        view.StartButtonControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsKeyFocusAllowed);

        // Close it again — key focus scope drops back to idle.
        view.StartButtonControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsKeyFocusAllowed);
    }


    // ── Handback is evidence-based, not call-site-opt-out (bevel-hx63) ────
    // The menu-close handback re-activates the app that was frontmost when the menu opened. It used to
    // fire unconditionally, so correctness depended on EVERY call site that activates a foreign window
    // remembering to call CancelKeyFocusHandback first. The group-flyout row did; the plain task-button
    // ActivateCommand did not — so "Start menu open → click a task button" light-dismissed Start and the
    // handback re-raised the previously-frontmost app (Terminal) back over the just-activated one (Arc),
    // with IgnoringOtherApps beating the helper's bare activate(). The decision now reads the live
    // frontmost app instead of trusting the call site.

    [Theory]
    // prior, frontmost, own, expected
    [InlineData(501, 501, 999, true)]   // prior app still frontmost → handback is a harmless no-op, keep it
    [InlineData(501, 999, 999, true)]   // only WE took front (menu became key) → restore the user's app
    [InlineData(501, 0, 999, true)]     // frontmost unknown → preserve the classic restore behaviour
    [InlineData(501, 777, 999, false)]  // a THIRD app is frontmost → we activated it; never stomp it
    [InlineData(0, 777, 999, false)]    // nothing captured / explicitly cancelled → nothing to hand back
    public void Key_focus_handback_only_runs_when_no_other_app_took_the_foreground(
        int priorAppPid, int frontmostPid, int ownPid, bool expected)
    {
        Assert.Equal(expected, TaskbarWindow.ShouldHandBackKeyFocus(priorAppPid, frontmostPid, ownPid));
    }

    // ── Every component slot is reachable by name (bevel-aqr7) ───────────

    // bevel-aqr7: every component slot must be reachable by name, surfaces and inert placeholders
    // included — a failed component that is invisible to a screen reader is worse than a visible gap.
    [AvaloniaFact]
    public void Every_component_slot_exposes_an_automation_name()
    {
        var inert = ComponentSlot.Inert(
            new ComponentInstance("x", "com.example.absent", new Dictionary<string, string>(), true),
            "not installed");
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(inert.Content)));

        var host = new SurfaceHost(new SurfaceOwnership());
        var surface = host.CreateView(
            new SurfacePrimitive("face", 16, 16, "Clock face", "Image"), "inst-a");
        Assert.Equal("Clock face", AutomationProperties.GetName(surface));
    }

    /// <summary>
    /// Whole-branch review Fix 8: when the type resolves (a failed-to-start, hung or quarantined
    /// component — as opposed to a genuinely uninstalled one), the Inert slot's automation name must
    /// announce the manifest's <c>DisplayName</c>, not the raw <c>TypeId</c>. A reverse-DNS string
    /// like <c>com.example.widget</c> is not something a screen reader should read aloud.
    /// </summary>
    [AvaloniaFact]
    public void An_inert_slot_announces_the_manifest_display_name_when_the_type_resolves()
    {
        var type = StackComponentManifest.Create();
        var inert = ComponentSlot.Inert(
            new ComponentInstance("x", type.Id, new Dictionary<string, string>(), true),
            "stopped responding", type);

        var name = AutomationProperties.GetName(inert.Content);
        Assert.Contains(type.DisplayName, name);
        Assert.DoesNotContain(type.Id, name);
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>Tray host exposing a single item, so the view realizes exactly one tray Image.</summary>
    private sealed class OneItemTray : ISystemTrayHost
    {
        private readonly TrayItem _item;
        public OneItemTray(TrayItem item) => _item = item;
        public Capabilities Capabilities => Capabilities.None;
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(new[] { _item });
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, bool park = false, CancellationToken ct = default)
            => Task.FromResult(true);
        public event EventHandler<TrayItem>? ItemAdded { add { } remove { } }
        public event EventHandler<TrayItem>? ItemRemoved { add { } remove { } }
        public event EventHandler<TrayItem>? ItemUpdated { add { } remove { } }
    }

    /// <summary>No-op window manager so a TaskItemViewModel's commands have a target.</summary>
    private sealed class NoopWindowManager : IWindowManager
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
