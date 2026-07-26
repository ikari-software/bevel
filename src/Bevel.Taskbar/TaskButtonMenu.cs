using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Bevel.Taskbar;

/// <summary>
/// Builds the app-centric taskbar-button context menu (bevel-ww71): the app icon + name header, a
/// "Windows ▶" submenu (each window → Activate / Minimize / Close), and Quit (or Force Quit when Option
/// is held WHILE right-clicking — decide-at-open; a live relabel is impossible, see Build). Built in code
/// (not XAML) so the per-window nesting + native modifier/dismiss hooks stay simple, and so one shape
/// covers single windows, groups, and windowless app-presence entries.
/// </summary>
public static class TaskButtonMenu
{
    /// <summary>Build + open the app-centric menu for a task button's data context. Returns the open
    /// flyout so the caller can dismiss it on app-deactivation (the taskbar is a non-activating window, so
    /// popups don't light-dismiss on app switch — TaskbarView hides this from OnAppDeactivated), or null
    /// for an unrecognized context.</summary>
    public static MenuFlyout? TryShow(Control button)
    {
        if (Build(button.DataContext) is not { } flyout) return null;
        flyout.ShowAt(button);   // anchored (not showAtPointer) — steadier light-dismiss coverage
        return flyout;
    }

    private static MenuFlyout? Build(object? dc)
    {
        var model = Describe(dc);
        if (model is null) return null;

        var header = new MenuItem { Header = model.AppName, IsEnabled = false };
        if (model.Icon is { } bmp)
            header.Icon = new Image { Source = bmp, Width = 16, Height = 16 };

        var items = new List<object> { header, new Separator() };

        if (model.Windows.Count > 0)
        {
            var windows = new MenuItem { Header = "_Windows" };
            foreach (var win in model.Windows)
            {
                var per = new MenuItem { Header = string.IsNullOrEmpty(win.Title) ? model.AppName : win.Title };
                per.Items.Add(new MenuItem { Header = "_Activate", Command = win.ActivateCommand });
                per.Items.Add(new MenuItem { Header = "Mi_nimize", Command = win.MinimizeCommand });
                per.Items.Add(new MenuItem { Header = "_Close", Command = win.CloseCommand });
                windows.Items.Add(per);
            }
            items.Add(windows);
            items.Add(new Separator());
        }

        // Decide-at-open: hold Option WHILE right-clicking → "Force Quit", else "Quit". A live relabel
        // while the menu is open is impossible here — the MenuFlyout popup does not repaint after it
        // settles (verified: content changes, TextBlock.Text, and IsVisible toggles all fail to redraw an
        // open popup), which is why any after-open swap either does nothing or shows both items. Reading
        // the modifier at open (TaskbarNative.OptionKeyDown, focus-independent) sets the single correct
        // item BEFORE the popup renders, so it's always right and needs no repaint.
        var force = TaskbarNative.OptionKeyDown();
        items.Add(new MenuItem
        {
            Header = force ? "_Force Quit" : "_Quit",
            Command = force ? model.ForceQuitCommand : model.QuitCommand,
        });

        var flyout = new MenuFlyout { ItemsSource = items };

        // Install a global mouse-down monitor while open — it fires only for clicks in OTHER apps, the
        // reliable click-outside dismiss for a non-activating window (Avalonia light-dismiss misses
        // cross-app clicks here).
        var monitor = IntPtr.Zero;
        flyout.Opened += (_, _) => monitor = TaskbarNative.AddGlobalMouseDownMonitor(() => Dispatcher.UIThread.Post(flyout.Hide));
        flyout.Closed += (_, _) => { TaskbarNative.RemoveMonitor(monitor); monitor = IntPtr.Zero; };
        return flyout;
    }

    // ── Adapt either button VM to a common menu shape ────────────────────────

    private sealed record WindowRow(string Title, ICommand ActivateCommand, ICommand MinimizeCommand, ICommand CloseCommand);
    private sealed record MenuModel(string AppName, Bitmap? Icon, IReadOnlyList<WindowRow> Windows, ICommand QuitCommand, ICommand ForceQuitCommand);

    private static MenuModel? Describe(object? dc) => dc switch
    {
        TaskGroupViewModel g => new MenuModel(
            g.DisplayName, g.IconSource,
            g.Windows.Select(Row).ToList(),
            g.QuitCommand, g.ForceQuitCommand),

        // App-presence has no windows → the Windows section is omitted (Windows is empty).
        TaskItemViewModel t => new MenuModel(
            TaskbarGrouping.DisplayName(t.AppId), t.IconSource,
            t.IsAppPresence ? Array.Empty<WindowRow>() : new[] { Row(t) },
            t.QuitCommand, t.ForceQuitCommand),

        _ => null,
    };

    private static WindowRow Row(TaskItemViewModel w) =>
        new(w.Title, w.ActivateCommand, w.MinimizeCommand, w.CloseCommand);
}
