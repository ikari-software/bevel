using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// FM-080: Item (file/folder) context menu.
/// Shows a Win2000 Explorer-style right-click menu whose contents are
/// determined by the target node's capabilities and registered verbs.
/// </summary>
public partial class ItemContextMenu : UserControl
{
    private ContextMenuActions _actions = new();

    public ItemContextMenu()
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
    /// Open the context menu for <paramref name="node"/> at the given
    /// viewport position. The menu is rebuilt on each open so that
    /// capability gating (Rename, Delete, etc.) matches the current node.
    /// </summary>
    public void Show(IVfsNode node, Control placement, Point position)
    {
        var menu = ContextMenuBuilder.BuildItemMenu(node, _actions);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Open(placement);
    }
}
