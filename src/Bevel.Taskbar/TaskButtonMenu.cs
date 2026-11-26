using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Builds the app-centric taskbar-button context menu (bevel-ww71): the app icon + name header, a
/// "Windows ▶" submenu (each window → Activate / Minimize / Restore / Maximize / Close), and Quit (or Force Quit when Option
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
    /// <summary>Height cap for the Tabs submenu — an Arc-style sidebar holds hundreds of tabs, so
    /// past ~24 rows the list SCROLLS (the Classic submenu template's SimpleMenuScrollViewer with
    /// its Win2000 repeat-arrow buttons engages as soon as content exceeds this).</summary>
    private const double TabListMaxHeight = 480;

    /// <summary>Sanity ceiling above the scroll cap: the Classic submenu panel is a plain
    /// non-virtualizing StackPanel, so every row REALIZES when the submenu opens — hundreds of
    /// MenuItems would turn the right-click into a visible hitch. Rows past this collapse into
    /// the disabled "… N more" tail.</summary>
    private const int MaxTabRows = 300;

    /// <summary>Upper bound on one tab activation against a foreign app (4 s mirrors the osascript
    /// child timeout; the AX path's walk observes the token cooperatively).</summary>
    private static readonly TimeSpan TabActivateBudget = TimeSpan.FromSeconds(4);

    /// <summary>One prefetched tab plus its favicon, decoded OFF the UI thread by the caller —
    /// Build runs on the UI thread and must only compose already-decoded bitmaps.</summary>
    public sealed record TabMenuRow(AppTab Tab, Bitmap? Icon);

    /// <summary>bevel-s5x: a per-app jump-list entry (header + action) for the button menu — the
    /// view supplies them via Build's appActions parameter: bundle id in, actions out.</summary>
    public sealed record TaskMenuAction(string Header, Action Invoke);

    public static MenuFlyout? TryShow(
        Control button, object? dc, IReadOnlyList<TabMenuRow>? tabs = null, ITabProvider? tabProvider = null,
        Action? onActivateForeign = null, Action? onActivationFailed = null,
        Func<string?, IReadOnlyList<TaskMenuAction>>? appActions = null,
        Action<string>? onShowInFinder = null)
    {
        // dc is passed in (not re-read from button.DataContext): the caller captured it before an
        // async tab prefetch and has verified the button still binds it — re-reading here would
        // reopen the race the caller just closed.
        if (Build(dc, tabs, tabProvider, onActivateForeign, onActivationFailed, appActions, onShowInFinder) is not { } flyout) return null;
        flyout.ShowAt(button);   // anchored (not showAtPointer) — steadier light-dismiss coverage
        return flyout;
    }

    /// <summary>Whether <paramref name="dc"/> is a context this menu understands — lets the caller
    /// decide (and mark the event handled) SYNCHRONOUSLY before any async tab prefetch.</summary>
    public static bool Recognizes(object? dc) => dc is TaskGroupViewModel or TaskItemViewModel;

    // internal (not private) so headless tests can assert the built shape without ShowAt.
    // onActivateForeign (bevel-nxic): invoked the instant a menu item that brings a FOREIGN window/app
    // to the front is chosen (window Activate, a Tab, or Quit/Force Quit) — the taskbar uses it to cancel
    // the key-focus handback so the menu's Closed doesn't re-raise the prior app over the new front.
    internal static MenuFlyout? Build(object? dc, IReadOnlyList<TabMenuRow>? tabs, ITabProvider? tabProvider,
        Action? onActivateForeign = null, Action? onActivationFailed = null,
        Func<string?, IReadOnlyList<TaskMenuAction>>? appActions = null,
        Action<string>? onShowInFinder = null)
    {
        var model = Describe(dc);
        if (model is null) return null;

        var header = new MenuItem { Header = model.AppName, IsEnabled = false };
        if (model.Icon is { } bmp)
            header.Icon = new Image { Source = bmp, Width = 16, Height = 16 };

        var items = new List<object> { header, new Separator() };

        // bevel-s5x: per-app jump-list actions render directly under the header separator, keyed on
        // the bundle id (AppId is only a display name).
        if (appActions?.Invoke(model.BundleId) is { Count: > 0 } actions)
        {
            foreach (var action in actions)
            {
                var captured = action;
                items.Add(new MenuItem
                {
                    Header = action.Header,
                    Command = new AsyncRelayCommand(() => { captured.Invoke(); return Task.CompletedTask; })
                });
            }
            items.Add(new Separator());
        }

        if (model.Windows.Count > 0)
        {
            var windows = new MenuItem { Header = "_Windows" };
            foreach (var win in model.Windows)
            {
                var per = new MenuItem { Header = string.IsNullOrEmpty(win.Title) ? model.AppName : win.Title };
                var activate = new MenuItem { Header = "_Activate", Command = win.ActivateCommand };
                if (onActivateForeign is not null) activate.Click += (_, _) => onActivateForeign();
                per.Items.Add(activate);
                per.Items.Add(new MenuItem { Header = "Mi_nimize", Command = win.MinimizeCommand });
                per.Items.Add(new MenuItem { Header = "_Restore", Command = win.RestoreCommand });
                // bevel-kpkx: the macOS green-button zoom (Windows: Maximize).
                per.Items.Add(new MenuItem { Header = "Ma_ximize", Command = win.ZoomCommand });
                per.Items.Add(new MenuItem { Header = "_Close", Command = win.CloseCommand });
                windows.Items.Add(per);
            }
            items.Add(windows);
            items.Add(new Separator());
            // bevel-apm: app-scoped verbs, only for a real group with something to act on. The commands
            // already exist on TaskGroupViewModel; this menu was the missing caller.
            if (model.IsGroup && model.Windows.Count >= 2)
            {
                items.Add(new MenuItem { Header = "Minimize _All", Command = model.MinimizeAllCommand });
                items.Add(new MenuItem { Header = "R_estore All", Command = model.RestoreAllCommand });
                items.Add(new MenuItem { Header = "Close A_ll", Command = model.CloseAllCommand });
                items.Add(new Separator());
            }
        }

        // Tabs (bevel-a40b): prefetched by the caller BEFORE the flyout opens — an open MenuFlyout
        // popup never repaints (see the Force Quit note below), so late-arriving rows would not show.
        if (tabs is { Count: > 0 } && tabProvider is not null)
        {
            var tabMenu = new MenuItem { Header = "_Tabs" };
            // Cap the submenu height so a hundreds-of-tabs sidebar scrolls (bevel-l17f) instead of
            // spanning the screen — the template's own menu scroll viewer takes it from there.
            // NOT x.Nesting(): a Style added to Control.Styles has no parent style, and the `^`
            // selector THROWS during template application — which the async click handler then
            // swallows, so the whole menu silently never opens (caught in review, reproduced live).
            tabMenu.Styles.Add(new Style(x => x.OfType<MenuItem>().Template().OfType<ScrollViewer>())
            {
                Setters = { new Setter(Avalonia.Layout.Layoutable.MaxHeightProperty, TabListMaxHeight) },
            });
            foreach (var row in tabs.Take(MaxTabRows))
            {
                var captured = row.Tab;
                var tabItem = new MenuItem
                {
                    Header = EscapeHeader(Ellipsize(captured.Title)),
                    // bevel-l17f: favicon when there is one; otherwise an iTerm session-tint swatch
                    // (code-drawn vector Border — no bitmap).
                    Icon = row.Icon is { } fav ? new Image { Source = fav, Width = 16, Height = 16 }
                        : TintSwatch(captured.Tint),
                    // Budgeted: activation walks a foreign app (AX tree / Apple Events) and a wedged
                    // target must cost a bounded threadpool wait, not an open-ended one.
                    Command = new AsyncRelayCommand(async () =>
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TabActivateBudget);
                        var outcome = await tabProvider.ActivateAsync(captured, cts.Token);
                        // bevel-bw1v: a non-activated outcome means the row was stale or the press
                        // failed — tell the caller so it can drop the prefetched list. The open
                        // popup never repaints, so the refresh lands on the NEXT menu open.
                        if (outcome != TabActivationResult.Activated) onActivationFailed?.Invoke();
                    }),
                };
                // Activating a tab raises its window + activates the app — a foreign front change, so
                // cancel the handback too (bevel-nxic).
                if (onActivateForeign is not null) tabItem.Click += (_, _) => onActivateForeign();
                tabMenu.Items.Add(tabItem);
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
        // IsMacOS guard (bevel-8kxc): TaskbarNative's static field initializers eagerly objc_getClass(…),
        // which force a libobjc.dylib load the instant the type is touched — a DllNotFound off-macOS. Bevel
        // only ships on macOS, but this menu is unit-tested headless on CI's Linux/Windows runners, so the
        // Option-key read (and the two monitor calls below) short-circuit there. `&&` never touches the type
        // when IsMacOS() is false, so the cctor never runs.
        // bevel-4nct: "Show in Finder" reveals the owning app's bundle in a file-manager window.
        // Offered only when the window carried a bundle path AND the caller wired a reveal hook —
        // never guessed when unknown, and never shown as a dead verb when hook-less.
        if (model.BundlePath is { } bundlePath && onShowInFinder is not null)
        {
            items.Add(new MenuItem
            {
                Header = "Show in _Finder",
                Command = new AsyncRelayCommand(() => { onShowInFinder(bundlePath); return Task.CompletedTask; })
            });
        }

        var force = OperatingSystem.IsMacOS() && TaskbarNative.OptionKeyDown();
        // bevel-d2j9: Hide sits directly above Quit (the macOS app-menu order). Hiding the target
        // hands the front to whatever the OS surfaces next, so — exactly like Quit — cancel the
        // key-focus handback (bevel-nxic).
        // Dead when there is no bundle id to key HideAppAsync on (HideAsync no-ops then) — keep the
        // row for a stable menu shape but grey it out (bevel-d2j9 review).
        var hide = new MenuItem { Header = "_Hide", Command = model.HideCommand, IsEnabled = !string.IsNullOrEmpty(model.BundleId) };
        // handledEventsToo: a MenuItem with a Command has a class handler (OnClick) that executes the
        // command and marks ClickEvent HANDLED before plain `Click +=` handlers run — without this the
        // handback-cancel would never fire (bevel-nxic).
        if (onActivateForeign is not null) hide.AddHandler(MenuItem.ClickEvent, (_, _) => onActivateForeign(), handledEventsToo: true);
        items.Add(hide);
        items.Add(new Separator());
        var quit = new MenuItem
        {
            Header = force ? "_Force Quit" : "_Quit",
            Command = force ? model.ForceQuitCommand : model.QuitCommand,
        };
        // Quitting the target hands the front to whatever the OS surfaces next; don't fight it by
        // re-raising the app that was frontmost when the menu opened (bevel-nxic).
        if (onActivateForeign is not null) quit.Click += (_, _) => onActivateForeign();
        items.Add(quit);

        var flyout = new MenuFlyout { ItemsSource = items };

        // Install a global mouse-down monitor while open — it fires only for clicks in OTHER apps, the
        // reliable click-outside dismiss for a non-activating window (Avalonia light-dismiss misses
        // cross-app clicks here).
        // Guarded for the same reason as OptionKeyDown above (bevel-8kxc): the global monitor is a macOS
        // AppKit facility; off-macOS there is no cross-app click stream to watch, so skip it entirely
        // rather than force the libobjc load through TaskbarNative.
        var monitor = IntPtr.Zero;
        if (OperatingSystem.IsMacOS())
        {
            flyout.Opened += (_, _) => monitor = TaskbarNative.AddGlobalMouseDownMonitor(() => Dispatcher.UIThread.Post(flyout.Hide));
            flyout.Closed += (_, _) => { TaskbarNative.RemoveMonitor(monitor); monitor = IntPtr.Zero; };
        }
        return flyout;
    }

    // ── Adapt either button VM to a common menu shape ────────────────────────

    private sealed record WindowRow(string Title, ICommand ActivateCommand, ICommand MinimizeCommand, ICommand RestoreCommand, ICommand ZoomCommand, ICommand CloseCommand);
    private sealed record MenuModel(string AppName, Bitmap? Icon, IReadOnlyList<WindowRow> Windows, ICommand QuitCommand, ICommand ForceQuitCommand,
        bool IsGroup = false, ICommand? MinimizeAllCommand = null, ICommand? RestoreAllCommand = null, ICommand? CloseAllCommand = null,
        string? BundleId = null, string? BundlePath = null, ICommand? HideCommand = null);

    private static MenuModel? Describe(object? dc) => dc switch
    {
        TaskGroupViewModel g => new MenuModel(
            g.DisplayName, g.IconSource,
            g.Windows.Select(Row).ToList(),
            g.QuitCommand, g.ForceQuitCommand,
            IsGroup: true, g.MinimizeAllCommand, g.RestoreAllCommand, g.CloseAllCommand,
            BundleId: g.BundleId, BundlePath: g.BundlePath, HideCommand: g.HideCommand),

        // App-presence has no windows → the Windows section is omitted (Windows is empty).
        TaskItemViewModel t => new MenuModel(
            TaskbarGrouping.DisplayName(t.AppId), t.IconSource,
            t.IsAppPresence ? Array.Empty<WindowRow>() : new[] { Row(t) },
            t.QuitCommand, t.ForceQuitCommand,
            BundleId: t.BundleId, BundlePath: t.BundlePath, HideCommand: t.HideCommand),

        _ => null,
    };

    private static WindowRow Row(TaskItemViewModel w) =>
        new(w.Title, w.ActivateCommand, w.MinimizeCommand, w.RestoreCommand, w.ZoomCommand, w.CloseCommand);

    /// <summary>Menu headers treat "_" as the access-key marker; tab titles are foreign text, so
    /// double any literal underscore to keep it visible (and un-hotkeyed).</summary>
    internal static string EscapeHeader(string title) => title.Replace("_", "__");

    /// <summary>bevel-l17f: a 12×12 colour swatch for iTerm rows without a favicon. The tint is external
    /// content (like a favicon), so a literal colour is allowed here; border uses the theme token.</summary>
    internal static Control? TintSwatch(string? tintHex)
    {
        if (tintHex is null || tintHex.Length != 7 || tintHex[0] != '#'
            || !int.TryParse(tintHex[1..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return null;
        var color = Avalonia.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
        return new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new Avalonia.CornerRadius(2),
            Background = new Avalonia.Media.SolidColorBrush(color),
            BorderBrush = Avalonia.Application.Current?.FindResource("Bevel.Brush.ButtonShadow") as Avalonia.Media.IBrush
                ?? Avalonia.Media.Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
        };
    }

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
