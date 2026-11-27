using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-4pgk: Favorites end-to-end — Ctrl+D adds the current folder to the persisted setting,
/// the Favorites menu lists it, the info pane gains a link, and the Organize dialog edits the setting.
/// Isolated settings dir; real temp folders.</summary>
public sealed class FavoritesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cfg;

    public FavoritesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-fav-{Guid.NewGuid():N}");
        _cfg = Path.Combine(Path.GetTempPath(), $"bevel-favcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_cfg);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_cfg, true); } catch { }
    }

    private static void Pump(int ms = 800)
    {
        var end = Environment.TickCount + ms;
        while (Environment.TickCount < end) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private FileManagerWindow NewWindow(SettingsService settings)
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.SetSettingsService(settings);
        win.SetController(new FileManagerController(root, new FileOperationService(root, new DefaultConflictHandler())));
        win.Show();
        return win;
    }

    private static void PressCtrlD(FileManagerWindow win)
    {
        win.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D,
            KeyModifiers = KeyModifiers.Control,
            Source = win,
        });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Ctrl_D_adds_the_current_folder_and_the_menu_lists_it()
    {
        var settings = new SettingsService(_cfg);
        await settings.LoadAsync();
        var win = NewWindow(settings);
        var path = new VfsPath("file", _dir);
        win.ActiveController!.NavigateTo(path);
        Pump();

        Assert.Empty(settings.Current.FilerFavorites);
        PressCtrlD(win);
        Pump();

        // Persisted as the VfsPath string, and the menu's saved-list section shows it.
        Assert.Equal(new[] { path.ToString() }, settings.Current.FilerFavorites);
        var menu = Field<MenuBar>(win, "MenuBar");
        var items = menu.FavoritesMenu.Items.Cast<object>().ToList();
        var sep = items.IndexOf(menu.GetType().GetField("FavoritesSeparator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(menu)!);
        Assert.True(sep >= 0 && items.Count > sep + 1, "a saved-list entry exists after the separator");
        var entry = items.OfType<MenuItem>().Last();
        Assert.Equal(new DirectoryInfo(_dir).Name, entry.Header as string);

        // Ctrl+D again is idempotent (FilerFavorites.Add rejects duplicates).
        PressCtrlD(win);
        Pump();
        Assert.Single(settings.Current.FilerFavorites);

        // The info pane link section grew: 2 fixed links + 1 favorite.
        var infoPane = Field<InfoPane>(win, "InfoPane");
        var links = (IList)Field<object>(infoPane, "_links");
        Assert.Equal(3, links.Count);
    }

    [AvaloniaFact]
    public async Task Organize_remove_updates_the_setting()
    {
        var settings = new SettingsService(_cfg);
        await settings.LoadAsync();
        await settings.UpdateAsync(s => s.FilerFavorites = new[] { "vfs://file//a", "vfs://file//b", "vfs://file//c" });

        var dialog = new OrganizeFavoritesDialog(settings);
        dialog.Show();
        Pump();
        Assert.Equal(3, dialog.List.ItemCount);

        dialog.List.SelectedIndex = 1;   // "vfs://file//b"
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.RemoveButton.IsEnabled);
        dialog.RemoveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump();

        Assert.Equal(new[] { "vfs://file//a", "vfs://file//c" }, settings.Current.FilerFavorites);
        Assert.Equal(2, dialog.List.ItemCount);

        // Move Up reorders in the setting too.
        dialog.List.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        dialog.UpButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump();
        Assert.Equal(new[] { "vfs://file//c", "vfs://file//a" }, settings.Current.FilerFavorites);
    }

    /// <summary>ce-code-review (bevel-4pgk): a hand-edited malformed 'filerFavorites' entry must be
    /// skipped, not throw — RefreshInfoPaneLinks runs inside the settings.Changed fan-out
    /// (ApplyFolderOptions), so one bad entry used to throw in every open Filer window.</summary>
    [AvaloniaFact]
    public async Task Malformed_stored_favorite_is_skipped_without_throwing()
    {
        var settings = new SettingsService(_cfg);
        await settings.LoadAsync();
        var good = new VfsPath("file", _dir).ToString();
        await settings.UpdateAsync(s => s.FilerFavorites = new[] { "garbage-no-scheme", good, "vfs://" });

        // Window setup navigates → UpdateInfoPane → RefreshInfoPaneLinks: must not throw.
        var win = NewWindow(settings);
        win.ActiveController!.NavigateTo(new VfsPath("file", _dir));
        Pump();

        // The settings.Changed fan-out path (ApplyFolderOptions → RefreshFavorites) must not throw either.
        win.ApplyFolderOptions();
        Pump();

        // Info pane: 2 fixed links + the single valid favorite; malformed entries were skipped.
        var infoPane = Field<InfoPane>(win, "InfoPane");
        var links = (IList)Field<object>(infoPane, "_links");
        Assert.Equal(3, links.Count);

        // Favorites menu: only the valid entry appears after the separator.
        var menu = Field<MenuBar>(win, "MenuBar");
        menu.FavoritesMenu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        Dispatcher.UIThread.RunJobs();
        var items = menu.FavoritesMenu.Items.Cast<object>().ToList();
        var sep = items.IndexOf(menu.GetType().GetField("FavoritesSeparator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(menu)!);
        Assert.True(sep >= 0, "favorites separator exists");
        Assert.Equal(1, items.Count - sep - 1);
    }
}
