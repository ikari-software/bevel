using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.FileManager.Components;
using Bevel.Pal.Abstractions;
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

    // ── Open With / Reveal (bevel-wxt) ─────────────────────────────────

    [AvaloniaFact]
    public void OpenWith_submenu_populates_lazily_and_routes_the_click()
    {
        var handlers = new List<OpenWithHandler>
        {
            new("TextEdit", "/System/Applications/TextEdit.app", null, IsDefault: true),
            new("Xcode", "/Applications/Xcode.app"),
        };
        OpenWithHandler? chosen = null;
        var actions = new ContextMenuActions
        {
            GetOpenWithHandlers = () => Task.FromResult<IReadOnlyList<OpenWithHandler>>(handlers),
            OpenWithApp = h => chosen = h,
        };

        var menu = ContextMenuBuilder.BuildItemMenu(FakeNode.File("a.txt"), actions);
        var openWith = Item(menu, "Open _With…");

        // Not populated until the submenu is first opened — only the placeholder holds the arrow.
        Assert.Single(openWith.Items.OfType<MenuItem>());

        openWith.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        Dispatcher.UIThread.RunJobs();

        var apps = openWith.Items.OfType<MenuItem>().ToList();
        Assert.Equal(2, apps.Count);
        Assert.Equal("TextEdit (default)", (string?)apps[0].Header);   // default flagged and sorted first
        Assert.Equal("Xcode", (string?)apps[1].Header);

        apps[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("Xcode", chosen?.AppName);
    }

    [AvaloniaFact]
    public void OpenWith_is_disabled_without_a_handler_source()
    {
        // No GetOpenWithHandlers wired → the submenu is inert (disabled, no placeholder children).
        var openWith = Item(ContextMenuBuilder.BuildItemMenu(FakeNode.File("a.txt"), new ContextMenuActions()), "Open _With…");
        Assert.False(openWith.IsEnabled);
    }

    [AvaloniaFact]
    public void Reveal_and_open_in_finder_are_enabled_only_when_wired()
    {
        var wired = ContextMenuBuilder.BuildItemMenu(FakeNode.File("a.txt"),
            new ContextMenuActions { RevealInFinder = () => { } });
        Assert.True(Item(wired, "Reveal in _Finder").IsEnabled);

        var bare = ContextMenuBuilder.BuildItemMenu(FakeNode.File("a.txt"), new ContextMenuActions());
        Assert.False(Item(bare, "Reveal in _Finder").IsEnabled);

        var folder = ContextMenuBuilder.BuildFolderBackgroundMenu(
            new ContextMenuActions { OpenLocationInFinder = () => { } });
        Assert.True(Item(folder, "_Open in Finder").IsEnabled);
    }
}
