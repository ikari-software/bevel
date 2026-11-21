using Avalonia.Automation;
using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class Toolbar : UserControl
{
    public Toolbar()
    {
        InitializeComponent();
    }

    // The icons are bitmaps rasterized from theme tokens (ToolbarIcons.Raster), so they are drawn when the
    // toolbar is attached and redrawn on the app-wide palette signal; drawing them once in the constructor
    // left an open Filer with the previous theme's icons (bevel-yf7c).
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bevel.UI.Glyphs.ThemeCacheInvalidated += ApplyIcons;
        ApplyIcons();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Bevel.UI.Glyphs.ThemeCacheInvalidated -= ApplyIcons;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Assign the self-drawn vector glyph AND a screen-reader name to each toolbar button.
    /// The buttons are icon-only, so without an explicit AutomationProperties.Name a screen reader
    /// announces "button" 15 times (ToolTip.Tip is not surfaced as the accessible name; bevel-6zs6).</summary>
    private void ApplyIcons()
    {
        Set(NavBack, ToolbarIcons.Back(), "Back");
        Set(NavForward, ToolbarIcons.Forward(), "Forward");
        Set(NavUp, ToolbarIcons.Up(), "Up one level");
        Set(SearchBtn, ToolbarIcons.Search(), "Search");
        Set(FoldersBtn, ToolbarIcons.Folders(), "Folders");
        Set(HistoryBtn, ToolbarIcons.History(), "History");
        Set(MoveToBtn, ToolbarIcons.MoveTo(), "Move to");
        Set(CopyToBtn, ToolbarIcons.CopyTo(), "Copy to");
        Set(CutBtn, ToolbarIcons.Cut(), "Cut");
        Set(CopyBtn, ToolbarIcons.Copy(), "Copy");
        Set(PasteBtn, ToolbarIcons.Paste(), "Paste");
        Set(UndoBtn, ToolbarIcons.Undo(), "Undo");
        Set(DeleteBtn, ToolbarIcons.Delete(), "Delete");
        Set(PropertiesBtn, ToolbarIcons.Properties(), "Properties");
        Set(ViewsBtn, ToolbarIcons.Views(), "Change view");

        static void Set(Classic.CommonControls.ToolBarButton button, Avalonia.Media.Imaging.Bitmap? icon, string name)
        {
            var previous = button.SmallIcon;
            button.SmallIcon = icon;
            if (!ReferenceEquals(previous, icon)) previous?.Dispose();   // the replaced raster is ours, and unshared
            AutomationProperties.SetName(button, name);
        }
    }

    // Navigation
    public Button BackButton => NavBack;
    public Button ForwardButton => NavForward;
    public Button UpButton => NavUp;

    // Action buttons
    public Button Search => SearchBtn;
    public Button Folders => FoldersBtn;
    public Button History => HistoryBtn;
    public Button MoveTo => MoveToBtn;
    public Button CopyTo => CopyToBtn;
    public Button Cut => CutBtn;
    public Button Copy => CopyBtn;
    public Button Paste => PasteBtn;
    public Button Undo => UndoBtn;
    public Button Delete => DeleteBtn;
    public Button Properties => PropertiesBtn;
    public Button Views => ViewsBtn;

    // View mode menu items
    public MenuItem ViewLargeIcons => MenuViewLargeIcons;
    public MenuItem ViewSmallIcons => MenuViewSmallIcons;
    public MenuItem ViewList => MenuViewList;
    public MenuItem ViewDetails => MenuViewDetails;
}