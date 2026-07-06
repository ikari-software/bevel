using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// Callbacks that context menu items invoke. The file-manager view implements
/// these and passes them in so the menus stay decoupled from the host window.
/// </summary>
public sealed class ContextMenuActions
{
    public Action? Open { get; init; }
    public Action? OpenWith { get; init; }
    public Action? SendTo { get; init; }
    public Action? Cut { get; init; }
    public Action? Copy { get; init; }
    public Action? Paste { get; init; }
    public Action? PasteShortcut { get; init; }
    public Action? CreateShortcut { get; init; }
    public Action? Delete { get; init; }
    public Action? Rename { get; init; }
    public Action? Properties { get; init; }
    public Action? Undo { get; init; }
    public Action? Refresh { get; init; }
    public Func<bool>? CanPaste { get; init; }
    public Func<bool>? CanUndo { get; init; }
    public Action<string>? ViewChanged { get; init; }
    public Action<string>? ArrangeIcons { get; init; }
    public Action<string>? NewItem { get; init; }
}

/// <summary>
/// Builds Win2000 Explorer-style context menus from VFS node capabilities.
/// FM-080 (item context menu), FM-081 (folder background menu).
/// </summary>
public static class ContextMenuBuilder
{
    private static readonly FontWeight BoldWeight = FontWeight.Bold;

    // ── FM-080: Item context menu ──────────────────────────────────────
    //
    // Order: default verb (bold) → other verbs → Open With → ---
    //        Send To → --- → Cut / Copy → --- → Create Shortcut /
    //        Delete / Rename → --- → Properties

    public static ContextMenu BuildItemMenu(IVfsNode node, ContextMenuActions actions)
    {
        var menu = new ContextMenu();
        var caps = node.Caps;
        var isFolder = node.Kind is VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot;

        // 1) Default verb – bold "Open" for files, "Explore" for folders
        var defaultVerb = isFolder ? "E_xplore" : "_Open";
        var defaultItem = MakeItem(defaultVerb, actions.Open, bold: true);
        defaultItem.IsEnabled = actions.Open is not null;
        menu.Items.Add(defaultItem);

        // 2) Open With submenu (FM-080)
        var openWith = new MenuItem { Header = "Open _With…" };
        openWith.IsEnabled = actions.OpenWith is not null;
        if (actions.OpenWith is not null)
            openWith.Click += (_, _) => actions.OpenWith();
        menu.Items.Add(openWith);

        menu.Items.Add(new Separator());

        // 3) Send To submenu (FM-080)
        var sendTo = new MenuItem { Header = "Sen_d To" };
        sendTo.IsEnabled = actions.SendTo is not null;
        if (actions.SendTo is not null)
            sendTo.Click += (_, _) => actions.SendTo();
        menu.Items.Add(sendTo);

        menu.Items.Add(new Separator());

        // 4) Cut / Copy
        var cut = MakeItem("Cu_t", actions.Cut);
        cut.IsEnabled = caps.HasFlag(VfsCapabilities.CopySource) && actions.Cut is not null;
        menu.Items.Add(cut);

        var copy = MakeItem("_Copy", actions.Copy);
        copy.IsEnabled = caps.HasFlag(VfsCapabilities.CopySource) && actions.Copy is not null;
        menu.Items.Add(copy);

        menu.Items.Add(new Separator());

        // 5) Create Shortcut / Delete / Rename
        var shortcut = MakeItem("Create _Shortcut", actions.CreateShortcut);
        shortcut.IsEnabled = actions.CreateShortcut is not null;
        menu.Items.Add(shortcut);

        var del = MakeItem("_Delete", actions.Delete, gesture: "Del");
        del.IsEnabled = (caps.HasFlag(VfsCapabilities.Delete) || caps.HasFlag(VfsCapabilities.Trash))
                        && actions.Delete is not null;
        menu.Items.Add(del);

        var rename = MakeItem("R_name", actions.Rename, gesture: "F2");
        rename.IsEnabled = caps.HasFlag(VfsCapabilities.Rename) && actions.Rename is not null;
        menu.Items.Add(rename);

        menu.Items.Add(new Separator());

        // 6) Properties
        var props = MakeItem("Propertie_s", actions.Properties, gesture: "Alt+Enter");
        props.IsEnabled = caps.HasFlag(VfsCapabilities.Properties) && actions.Properties is not null;
        menu.Items.Add(props);

        return menu;
    }

    // ── FM-081: Folder background context menu ─────────────────────────
    //
    // Order: View → Arrange Icons → Refresh → --- → Paste /
    //        Paste Shortcut / Undo → --- → New → --- → Properties

    public static ContextMenu BuildFolderBackgroundMenu(ContextMenuActions actions)
    {
        var menu = new ContextMenu();

        // 1) View submenu
        var view = new MenuItem { Header = "_View" };
        foreach (var (label, key) in new[]
        {
            ("Lar_ge Icons", "large-icons"),
            ("S_mall Icons", "small-icons"),
            ("_List", "list"),
            ("_Details", "details"),
        })
        {
            var item = new MenuItem { Header = label };
            var capturedKey = key;
            if (actions.ViewChanged is not null)
                item.Click += (_, _) => actions.ViewChanged(capturedKey);
            view.Items.Add(item);
        }
        menu.Items.Add(view);

        // 2) Arrange Icons submenu
        var arrange = new MenuItem { Header = "Arrange _Icons" };
        var arrangeKeys = new[] { "name", "type", "size", "date" };
        var arrangeLabels = new[] { "by _Name", "by _Type", "by _Size", "by _Date" };
        for (int i = 0; i < arrangeKeys.Length; i++)
        {
            var item = new MenuItem { Header = arrangeLabels[i] };
            var capturedKey = arrangeKeys[i];
            if (actions.ArrangeIcons is not null)
                item.Click += (_, _) => actions.ArrangeIcons(capturedKey);
            arrange.Items.Add(item);
        }
        menu.Items.Add(arrange);

        // 3) Refresh
        var refresh = MakeItem("_Refresh", actions.Refresh, gesture: "F5");
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        // 4) Paste / Paste Shortcut / Undo
        var paste = MakeItem("_Paste", actions.Paste, gesture: "Ctrl+V");
        paste.IsEnabled = actions.CanPaste?.Invoke() == true && actions.Paste is not null;
        menu.Items.Add(paste);

        var pasteShortcut = MakeItem("Paste S_hortcut", actions.PasteShortcut);
        pasteShortcut.IsEnabled = actions.CanPaste?.Invoke() == true && actions.PasteShortcut is not null;
        menu.Items.Add(pasteShortcut);

        var undo = MakeItem("_Undo", actions.Undo, gesture: "Ctrl+Z");
        undo.IsEnabled = actions.CanUndo?.Invoke() == true && actions.Undo is not null;
        menu.Items.Add(undo);

        menu.Items.Add(new Separator());

        // 5) New submenu
        var newMenu = new MenuItem { Header = "_New" };
        foreach (var (label, kind) in new[]
        {
            ("_Folder", "folder"),
            ("_Text Document", "text-document"),
        })
        {
            var item = new MenuItem { Header = label };
            var capturedKind = kind;
            if (actions.NewItem is not null)
                item.Click += (_, _) => actions.NewItem(capturedKind);
            newMenu.Items.Add(item);
        }
        menu.Items.Add(newMenu);

        menu.Items.Add(new Separator());

        // 6) Properties
        var props = MakeItem("Propertie_s", actions.Properties, gesture: "Alt+Enter");
        props.IsEnabled = actions.Properties is not null;
        menu.Items.Add(props);

        return menu;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static MenuItem MakeItem(
        string header,
        Action? action,
        string? gesture = null,
        bool bold = false)
    {
        var item = new MenuItem { Header = header };

        if (bold)
            item.FontWeight = BoldWeight;

        if (gesture is not null)
            item.InputGesture = new KeyGesture(Key.None); // display-only; real binding lives elsewhere

        if (action is not null)
            item.Click += (_, _) => action();

        return item;
    }
}
