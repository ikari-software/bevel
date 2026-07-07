using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class Toolbar : UserControl
{
    public Toolbar()
    {
        InitializeComponent();
        ApplyIcons();
    }

    /// <summary>Assign the self-drawn vector glyphs to each toolbar button's SmallIcon slot.</summary>
    private void ApplyIcons()
    {
        NavBack.SmallIcon = ToolbarIcons.Back();
        NavForward.SmallIcon = ToolbarIcons.Forward();
        NavUp.SmallIcon = ToolbarIcons.Up();
        SearchBtn.SmallIcon = ToolbarIcons.Search();
        FoldersBtn.SmallIcon = ToolbarIcons.Folders();
        HistoryBtn.SmallIcon = ToolbarIcons.History();
        MoveToBtn.SmallIcon = ToolbarIcons.MoveTo();
        CopyToBtn.SmallIcon = ToolbarIcons.CopyTo();
        CutBtn.SmallIcon = ToolbarIcons.Cut();
        CopyBtn.SmallIcon = ToolbarIcons.Copy();
        PasteBtn.SmallIcon = ToolbarIcons.Paste();
        UndoBtn.SmallIcon = ToolbarIcons.Undo();
        DeleteBtn.SmallIcon = ToolbarIcons.Delete();
        PropertiesBtn.SmallIcon = ToolbarIcons.Properties();
        ViewsBtn.SmallIcon = ToolbarIcons.Views();
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