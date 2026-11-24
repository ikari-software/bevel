using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class MenuBar : UserControl
{
    public MenuBar() => InitializeComponent();

    // File menu
    public MenuItem NewWindow => NewWindowItem;
    public MenuItem NewTab => NewTabItem;
    public MenuItem Open => OpenItem;
    public MenuItem OpenWith => OpenWithItem;
    public MenuItem MoveToFolder => MoveToFolderItem;
    public MenuItem CopyToFolder => CopyToFolderItem;
    public MenuItem CreateShortcut => CreateShortcutItem;
    public MenuItem Delete => DeleteItem;
    public MenuItem Rename => RenameItem;
    public MenuItem Properties => PropertiesItem;
    public MenuItem CloseWindow => CloseWindowItem;

    // Edit menu
    public MenuItem Undo => UndoDeleteItem;
    public MenuItem Cut => CutItem;
    public MenuItem Copy => CopyItem;
    public MenuItem Paste => PasteItem;
    public MenuItem SelectAll => SelectAllItem;
    public MenuItem InvertSelection => InvertSelectionItem;
    public MenuItem FindFiles => FindFilesItem;

    // View menu
    public MenuItem ViewLargeIcons => LargeIconsItem;
    public MenuItem ViewSmallIcons => SmallIconsItem;
    public MenuItem ViewList => ListItem;
    public MenuItem ViewDetails => DetailsItem;
    public MenuItem Refresh => RefreshItem;

    // Go menu
    public MenuItem GoBack => BackItem;
    public MenuItem GoForward => ForwardItem;
    public MenuItem GoUp => UpItem;
    public MenuItem GoHome => HomeItem;
    public MenuItem GoMyComputer => MyComputerItem;

    // Tools menu
    public MenuItem FolderOptions => FolderOptionsItem;

    // Favorites menu (bevel-4pgk)
    public MenuItem FavoritesMenu => FavoritesMenuItem;
    public MenuItem AddToFavorites => AddToFavoritesItem;
    public MenuItem OrganizeFavorites => OrganizeFavoritesItem;

    /// <summary>Rebuild the Favorites submenu's saved-list section (everything after the separator) from
    /// <paramref name="paths"/> (VfsPath.ToString() values); clicking an entry calls
    /// <paramref name="onOpen"/> with the raw path string. Called on SubmenuOpened so the list is always
    /// current when the menu drops down.</summary>
    public void RebuildFavorites(IReadOnlyList<string> paths, Action<string> onOpen)
    {
        var items = FavoritesMenuItem.Items;
        var sep = items.IndexOf(FavoritesSeparator);
        if (sep < 0) return;
        while (items.Count > sep + 1)
            items.RemoveAt(items.Count - 1);

        if (paths.Count == 0)
        {
            items.Add(new MenuItem { Header = "(no favorites yet)", IsEnabled = false });
            return;
        }
        foreach (var p in paths)
        {
            var captured = p;
            // "_" is the access-key marker in menu headers; favorite labels are file names, so double any.
            var label = Bevel.FileManager.Navigation.HistoryEntry.LabelFor(Core.Vfs.VfsPath.Parse(captured))
                .Replace("_", "__");
            var entry = new MenuItem { Header = label };
            entry.Click += (_, _) => onOpen(captured);
            items.Add(entry);
        }
    }
}