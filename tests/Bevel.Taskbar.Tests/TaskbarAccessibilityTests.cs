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
using Bevel.Pal.Abstractions;
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
        using var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = view.StartButtonControl;
        Assert.True(start.Focusable);
        Assert.Equal("Start", AutomationProperties.GetName(start));
    }

    [AvaloniaFact]
    public void A_task_button_is_focusable_with_its_window_title_as_the_accessible_name()
    {
        using var model = new ShellModel(null, null, null);
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
        using var model = new ShellModel(null, null, null);
        var host = new OneItemTray(new TrayItem(new TrayItemId("1:10"), "Wi-Fi"));
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model), tray: host);
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var trayImage = window.GetVisualDescendants().OfType<Image>()
            .FirstOrDefault(i => i.DataContext is TrayItemViewModel);
        Assert.NotNull(trayImage);
        Assert.True(trayImage!.Focusable);
        Assert.Equal("Wi-Fi", AutomationProperties.GetName(trayImage));
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
        using var model = new ShellModel(null, null, null);
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
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, CancellationToken ct = default)
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
