using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// FM-081: Folder-background context menu.
/// Populates itself from ContextMenuActions each time it opens so that
/// enabled/disabled state (Paste, Undo) stays current.
/// </summary>
public partial class FolderContextMenu : UserControl
{
    private ContextMenuActions _actions = new();

    public FolderContextMenu()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Set (or replace) the action set that menu items invoke.
    /// </summary>
    public void SetActions(ContextMenuActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <summary>
    /// Open the context menu at the given viewport point relative to
    /// <paramref name="placement"/>. The menu is rebuilt on each open
    /// so enabled/disabled reflects the current clipboard and undo state.
    /// </summary>
    public void Show(Control placement, Point position)
    {
        var menu = ContextMenuBuilder.BuildFolderBackgroundMenu(_actions);
        menu.Placement = PlacementMode.Pointer;   // open at the cursor, not the corner of the list
        menu.Open(placement);
    }
}
