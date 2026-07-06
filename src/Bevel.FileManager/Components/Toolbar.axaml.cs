using Avalonia.Controls;

namespace Bevel.FileManager.Components;

public partial class Toolbar : UserControl
{
    public Toolbar() => InitializeComponent();

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