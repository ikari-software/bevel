using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

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
    /// <summary>Tab rows shown before collapsing into a disabled "&#8230; N more" summary row — an
    /// Arc-style sidebar holds hundreds of tabs and a menu must not.</summary>
    private const int MaxTabRows = 25;

    /// <summary>Upper bound on one tab activation against a foreign app (4 s mirrors the osascript
    /// child timeout; the AX path's walk observes the token cooperatively).</summary>
    private static readonly TimeSpan TabActivateBudget = TimeSpan.FromSeconds(4);

    public static MenuFlyout? TryShow(
        Control button, object? dc, IReadOnlyList<AppTab>? tabs = null, ITabProvider? tabProvider = null)
    {
        // dc is passed in (not re-read from button.DataContext): the caller captured it before an
        // async tab prefetch and has verified the button still binds it — re-reading here would
        // reopen the race the caller just closed.
        if (Build(dc, tabs, tabProvider) is not { } flyout) return null;
        flyout.ShowAt(button);   // anchored (not showAtPointer) — steadier light-dismiss coverage
        return flyout;
    }

    /// <summary>Whether <paramref name="dc"/> is a context this menu understands — lets the caller
    /// decide (and mark the event handled) SYNCHRONOUSLY before any async tab prefetch.</summary>
    public static bool Recognizes(object? dc) => dc is TaskGroupViewModel or TaskItemViewModel;

    // internal (not private) so headless tests can assert the built shape without ShowAt.
    internal static MenuFlyout? Build(object? dc, IReadOnlyList<AppTab>? tabs, ITabProvider? tabProvider)
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

        // Tabs (bevel-a40b): prefetched by the caller BEFORE the flyout opens — an open MenuFlyout
        // popup never repaints (see the Force Quit note below), so late-arriving rows would not show.
        if (tabs is { Count: > 0 } && tabProvider is not null)
        {
            var tabMenu = new MenuItem { Header = "_Tabs" };
            foreach (var tab in tabs.Take(MaxTabRows))
            {
                var captured = tab;
                tabMenu.Items.Add(new MenuItem
                {
                    Header = EscapeHeader(Ellipsize(captured.Title)),
                    // Budgeted: activation walks a foreign app (AX tree / Apple Events) and a wedged
                    // target must cost a bounded threadpool wait, not an open-ended one.
                    Command = new AsyncRelayCommand(async () =>
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TabActivateBudget);
                        await tabProvider.ActivateAsync(captured, cts.Token);
                    }),
                });
            }
            if (tabs.Count > MaxTabRows)
                tabMenu.Items.Add(new MenuItem { Header = $"… {tabs.Count - MaxTabRows} more", IsEnabled = false });
            items.Add(tabMenu);
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

    /// <summary>Menu headers treat "_" as the access-key marker; tab titles are foreign text, so
    /// double any literal underscore to keep it visible (and un-hotkeyed).</summary>
    internal static string EscapeHeader(string title) => title.Replace("_", "__");

    /// <summary>Single-line cap for foreign tab titles so one verbose page can't stretch the menu.
    /// The cut backs off a high surrogate so an emoji-leading title never leaves half a pair
    /// (a lone surrogate renders as a replacement glyph).</summary>
    internal static string Ellipsize(string title, int max = 70)
    {
        if (title.Length <= max) return title;
        var cut = max - 1;
        if (char.IsHighSurrogate(title[cut - 1])) cut--;
        return title[..cut].TrimEnd() + "…";
    }
}
