using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Drag and drop to and from the Filer (bevel-u2x6).
///
/// <para>A platform drag session cannot run headless, so <c>StartDragAsync</c> itself is not driven
/// here. Everything it decides is: the payload it builds, the effects it offers, how paths resolve to
/// storage items, what a drop resolves to, and which file operation a drop performs. The drop side
/// is driven end to end by raising real <see cref="DragDrop.DropEvent"/>s on a shown view.</para>
/// </summary>
public sealed class ItemViewDragDropTests
{
    // ── Fakes ────────────────────────────────────────────────────────

    /// <summary>A storage item that is only a name and a URI — all the drag code reads.</summary>
    public class FakeStorageItem : DispatchProxy
    {
        public Uri Uri = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Path" => Uri,
            "get_Name" => System.IO.Path.GetFileName(Uri.LocalPath),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };

        public static IStorageFile File(string uri) => Make<IStorageFile>(uri);
        public static IStorageFolder Folder(string uri) => Make<IStorageFolder>(uri);

        private static T Make<T>(string uri) where T : class
        {
            var proxy = Create<T, FakeStorageItem>();
            ((FakeStorageItem)(object)proxy).Uri = new Uri(uri, UriKind.RelativeOrAbsolute);
            return proxy;
        }
    }

    /// <summary>A storage provider that records which lookup each URI went through.</summary>
    public class RecordingStorageProvider : DispatchProxy
    {
        public readonly List<(string Api, Uri Uri)> Calls = new();
        public Func<Uri, bool> Exists = _ => true;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IStorageProvider.TryGetFileFromPathAsync):
                {
                    var uri = (Uri)args![0]!;
                    Calls.Add(("file", uri));
                    return Task.FromResult(Exists(uri) ? FakeStorageItem.File(uri.AbsoluteUri) : null);
                }
                case nameof(IStorageProvider.TryGetFolderFromPathAsync):
                {
                    var uri = (Uri)args![0]!;
                    Calls.Add(("folder", uri));
                    return Task.FromResult(Exists(uri) ? FakeStorageItem.Folder(uri.AbsoluteUri) : null);
                }
                default:
                    throw new NotSupportedException(method.Name);
            }
        }

        public static (IStorageProvider Provider, RecordingStorageProvider Recorder) Create()
        {
            var proxy = Create<IStorageProvider, RecordingStorageProvider>();
            return (proxy, (RecordingStorageProvider)(object)proxy);
        }
    }

    private static DataTransfer Files(params string[] uris)
    {
        var transfer = new DataTransfer();
        foreach (var uri in uris)
        {
            var item = new DataTransferItem();
            item.SetFile(FakeStorageItem.File(uri));
            transfer.Add(item);
        }
        return transfer;
    }

    private const DragDropEffects Any = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    // ── Drag source ──────────────────────────────────────────────────

    [Fact]
    public async Task Folders_resolve_through_the_folder_api_and_files_through_the_file_api()
    {
        // Asking for a FILE at a directory path returns null, so a mixed selection would drag only
        // its files and silently leave the folders behind.
        var (provider, recorder) = RecordingStorageProvider.Create();

        var items = await ItemView.ResolveStorageItemsAsync(provider,
            new[] { ("/a/doc.txt", false), ("/a/photos", true) });

        Assert.Equal(new[] { "file", "folder" }, recorder.Calls.Select(c => c.Api));
        Assert.IsAssignableFrom<IStorageFile>(items[0]);
        Assert.IsAssignableFrom<IStorageFolder>(items[1]);
    }

    [Fact]
    public async Task A_name_with_url_metacharacters_resolves_to_the_whole_path()
    {
        var (provider, recorder) = RecordingStorageProvider.Create();

        await ItemView.ResolveStorageItemsAsync(provider, new[] { ("/a/50% off?.txt", false), ("/a/notes #3", true) });

        Assert.Equal(new[] { "/a/50% off?.txt", "/a/notes #3" }, recorder.Calls.Select(c => c.Uri.LocalPath));
    }

    [Fact]
    public async Task Items_the_provider_cannot_find_are_left_out()
    {
        var (provider, recorder) = RecordingStorageProvider.Create();
        recorder.Exists = uri => !uri.LocalPath.EndsWith("gone.txt", StringComparison.Ordinal);

        var items = await ItemView.ResolveStorageItemsAsync(provider,
            new[] { ("/a/here.txt", false), ("/a/gone.txt", false) });

        Assert.Equal("/a/here.txt", Assert.Single(items).Path.LocalPath);
    }

    [Fact]
    public async Task No_provider_or_no_selection_resolves_nothing()
    {
        var (provider, _) = RecordingStorageProvider.Create();
        Assert.Empty(await ItemView.ResolveStorageItemsAsync(null, new[] { ("/a/x", false) }));
        Assert.Empty(await ItemView.ResolveStorageItemsAsync(provider, Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void The_drag_payload_is_one_file_item_per_storage_item_and_nothing_else()
    {
        // Any further item (text, an application format) hangs the native drag session on macOS.
        var a = FakeStorageItem.File("file:///a/1.txt");
        var b = FakeStorageItem.Folder("file:///a/dir");

        var transfer = ItemView.BuildDragTransfer(new IStorageItem[] { a, b });

        Assert.Equal(2, transfer.Items.Count);
        Assert.All(transfer.Items, i => Assert.Equal(new[] { DataFormat.File }, i.Formats));
        Assert.Equal(new IStorageItem[] { a, b }, transfer.TryGetFiles());
        Assert.Null(transfer.TryGetText());
    }

    [Fact]
    public void A_drag_out_offers_copy_only()
    {
        // Offering Move or Link hangs the native drag session on macOS (bevel-u2x6).
        Assert.Equal(DragDropEffects.Copy, ItemView.DragSourceEffects);
    }

    // ── Drop payload ─────────────────────────────────────────────────

    [Fact]
    public void A_dropped_os_file_list_resolves_to_file_paths_in_order()
    {
        var result = ItemView.ResolveDropPaths(Files("file:///Users/test/a.txt", "file:///Users/test/b%20c.txt"));

        Assert.Equal(new[] { new VfsPath("file", "/Users/test/a.txt"), new VfsPath("file", "/Users/test/b c.txt") }, result);
    }

    [Fact]
    public void Dropped_items_that_are_not_local_files_are_ignored()
    {
        // A provider-backed item has no path a file operation could act on; reading LocalPath on a
        // relative URI would throw inside the drop handler.
        var result = ItemView.ResolveDropPaths(Files("content://provider/doc/1", "relative/name.txt", "file:///ok.txt"));

        Assert.Equal(new[] { new VfsPath("file", "/ok.txt") }, result);
        Assert.Null(ItemView.ResolveDropPaths(Files("https://example.com/x.txt")));
    }

    [Fact]
    public void Plain_text_is_never_read_as_paths()
    {
        var transfer = new DataTransfer();
        var text = new DataTransferItem();
        text.SetText("/Users/test/notAFile.txt\n/Users/test/alsoNot.txt");
        transfer.Add(text);

        Assert.Null(ItemView.ResolveDropPaths(transfer));
    }

    [Fact]
    public void An_empty_or_missing_transfer_resolves_to_nothing()
    {
        Assert.Null(ItemView.ResolveDropPaths(new DataTransfer()));
        Assert.Null(ItemView.ResolveDropPaths(null));
    }

    // ── Which operation a drop performs ──────────────────────────────

    [Theory]
    // A drag from another app: copy by default. This is the rule that keeps a plain drop from
    // Finder or Mail from removing the file where it came from.
    [InlineData(KeyModifiers.None, false, Any, DropOperation.Copy)]
    [InlineData(KeyModifiers.Control, false, Any, DropOperation.Copy)]
    [InlineData(KeyModifiers.Alt, false, Any, DropOperation.Copy)]
    [InlineData(KeyModifiers.Shift, false, Any, DropOperation.Move)]
    [InlineData(KeyModifiers.Meta, false, Any, DropOperation.Move)]
    // ...and a requested move the source does not allow stays a copy.
    [InlineData(KeyModifiers.Shift, false, DragDropEffects.Copy, DropOperation.Copy)]
    // Our own drag: the classic within-window move, unless a copy modifier is held.
    [InlineData(KeyModifiers.None, true, DragDropEffects.Copy, DropOperation.Move)]
    [InlineData(KeyModifiers.Shift, true, DragDropEffects.Copy, DropOperation.Move)]
    [InlineData(KeyModifiers.Control, true, DragDropEffects.Copy, DropOperation.Copy)]
    [InlineData(KeyModifiers.Alt, true, DragDropEffects.Copy, DropOperation.Copy)]
    // Ctrl+Shift ("create shortcut") has no link operation behind it yet: it copies, never moves.
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, false, Any, DropOperation.Copy)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, true, DragDropEffects.Copy, DropOperation.Copy)]
    public void The_drop_operation_follows_the_source_and_the_modifiers(
        KeyModifiers modifiers, bool ownDrag, DragDropEffects allowed, DropOperation expected)
    {
        Assert.Equal(expected, ItemView.DecideDropOperation(modifiers, ownDrag, allowed));
    }

    [Theory]
    [InlineData(true, DropOperation.Copy, Any, DragDropEffects.Copy)]
    [InlineData(true, DropOperation.Move, Any, DragDropEffects.Move)]
    // Our own drag offers Copy only; answering Move would refuse the drop.
    [InlineData(true, DropOperation.Move, DragDropEffects.Copy, DragDropEffects.Copy)]
    [InlineData(false, DropOperation.Copy, Any, DragDropEffects.None)]
    public void The_cursor_effect_matches_the_operation(
        bool hasFiles, DropOperation operation, DragDropEffects allowed, DragDropEffects expected)
    {
        Assert.Equal(expected, ItemView.DragOverEffect(hasFiles, operation, allowed));
    }

    // ── The view, end to end ─────────────────────────────────────────

    private static ItemView ShownView()
    {
        var view = new ItemView();
        new Window { Content = view, Width = 400, Height = 300 }.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static DragEventArgs Raise(ItemView view, RoutedEvent<DragEventArgs> routedEvent,
        IDataTransfer transfer, KeyModifiers modifiers, DragDropEffects allowed)
    {
        var args = new DragEventArgs(routedEvent, transfer, view, new Point(200, 150), modifiers)
        {
            DragEffects = allowed,
        };
        view.RaiseEvent(args);
        return args;
    }

    [AvaloniaFact]
    public void The_view_accepts_drops()
    {
        // Without AllowDrop the OS never routes drag events to the control and every drop is a
        // silent no-op. This was the root cause of bevel-u2x6.
        Assert.True(DragDrop.GetAllowDrop(ShownView()));
    }

    [AvaloniaFact]
    public void A_plain_drop_from_another_app_requests_a_copy_into_the_current_folder()
    {
        var view = ShownView();
        DropEventArgs? dropped = null;
        view.DropRequested += (_, e) => dropped = e;

        var args = Raise(view, DragDrop.DropEvent, Files("file:///Users/test/report.pdf"), KeyModifiers.None, Any);

        Assert.NotNull(dropped);
        Assert.Equal(DropOperation.Copy, dropped!.Operation);
        Assert.Equal(new[] { new VfsPath("file", "/Users/test/report.pdf") }, dropped.Paths);
        Assert.Null(dropped.TargetFolder);
        Assert.True(args.Handled);
    }

    [AvaloniaFact]
    public void A_shift_drop_from_another_app_requests_a_move()
    {
        var view = ShownView();
        DropEventArgs? dropped = null;
        view.DropRequested += (_, e) => dropped = e;

        Raise(view, DragDrop.DropEvent, Files("file:///Users/test/report.pdf"), KeyModifiers.Shift, Any);

        Assert.Equal(DropOperation.Move, dropped?.Operation);
    }

    [AvaloniaFact]
    public void A_drop_without_files_requests_nothing()
    {
        var view = ShownView();
        var raised = false;
        view.DropRequested += (_, _) => raised = true;
        var transfer = new DataTransfer();
        var text = new DataTransferItem();
        text.SetText("/Users/test/report.pdf");
        transfer.Add(text);

        Raise(view, DragDrop.DropEvent, transfer, KeyModifiers.None, Any);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void Dragging_files_over_the_view_shows_the_effect_the_drop_will_have()
    {
        var view = ShownView();
        var files = Files("file:///Users/test/report.pdf");

        Assert.Equal(DragDropEffects.Copy, Raise(view, DragDrop.DragOverEvent, files, KeyModifiers.None, Any).DragEffects);
        Assert.Equal(DragDropEffects.Move, Raise(view, DragDrop.DragOverEvent, files, KeyModifiers.Shift, Any).DragEffects);
        Assert.Equal(DragDropEffects.None, Raise(view, DragDrop.DragOverEvent, new DataTransfer(), KeyModifiers.None, Any).DragEffects);
    }

    // ── What the window does with a drop ─────────────────────────────

    [Fact]
    public void A_drop_leaves_out_the_destination_itself_and_items_already_in_it()
    {
        var dest = new VfsPath("file", "/Users/test/docs");
        var sources = new[]
        {
            new VfsPath("file", "/Users/test/docs"),          // a folder dropped onto its own row
            new VfsPath("file", "/Users/test/docs/a.txt"),    // released where it started
            new VfsPath("file", "/Users/test/other/b.txt"),
            new VfsPath("trash", "/Users/test/docs/c.txt"),   // same value, different scheme: a real source
        };

        Assert.Equal(new[] { sources[2], sources[3] }, FileManagerWindow.DropSources(sources, dest));
    }

    [Fact]
    public void A_drop_into_the_root_leaves_out_top_level_items_whichever_way_the_root_is_spelled()
    {
        // "/Users".Parent is the empty-valued root, while a navigated root is "/".
        var top = new VfsPath("file", "/Users");
        Assert.Empty(FileManagerWindow.DropSources(new[] { top }, new VfsPath("file", "/")));
        Assert.Empty(FileManagerWindow.DropSources(new[] { top }, VfsPath.Root("file")));
    }
}
