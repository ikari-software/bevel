using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Locks the context-menu shortcut hints (bevel-972 / review #19): gesture text passed to the
/// builder must surface as a parsed <see cref="KeyGesture"/> on the item — a regression here
/// renders the Win2000 menus without their right-aligned accelerator column.
/// </summary>
public sealed class ContextMenuBuilderTests
{
    private static MenuItem Item(ContextMenu menu, string header)
        => menu.Items.OfType<MenuItem>().First(m => (string?)m.Header == header);

    [AvaloniaFact]
    public void Item_menu_shows_accelerator_hints_for_gestured_entries()
    {
        var menu = ContextMenuBuilder.BuildItemMenu(FakeNode.File("a.txt"), new ContextMenuActions());

        Assert.Equal(KeyGesture.Parse("Ctrl+X"), Item(menu, "Cu_t").InputGesture);
        Assert.Equal(KeyGesture.Parse("Ctrl+C"), Item(menu, "_Copy").InputGesture);
        Assert.Equal(KeyGesture.Parse("Delete"), Item(menu, "_Delete").InputGesture);
        Assert.Equal(KeyGesture.Parse("F2"), Item(menu, "R_name").InputGesture);
        Assert.Equal(KeyGesture.Parse("Alt+Enter"), Item(menu, "Propertie_s").InputGesture);
        Assert.Null(Item(menu, "Create _Shortcut").InputGesture); // no accelerator in Win2000
    }

    [AvaloniaFact]
    public void Folder_background_menu_shows_accelerator_hints_for_gestured_entries()
    {
        var menu = ContextMenuBuilder.BuildFolderBackgroundMenu(new ContextMenuActions());

        Assert.Equal(KeyGesture.Parse("F5"), Item(menu, "_Refresh").InputGesture);
        Assert.Equal(KeyGesture.Parse("Ctrl+V"), Item(menu, "_Paste").InputGesture);
        Assert.Equal(KeyGesture.Parse("Ctrl+Z"), Item(menu, "_Undo").InputGesture);
    }
}
