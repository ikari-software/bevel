using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class MenuBar : UserControl
{
    public MenuBar() => InitializeComponent();

    // File menu
    public MenuItem NewWindow => NewWindowItem;
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
}