using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace Bevel.Taskbar;

/// <summary>
/// A folder stack's flyout content (bevel-9elh): the macOS-Dock-style grid of recent items with
/// content previews, plus the retained "Open folder" row. Split out of <c>TaskbarView</c> so the grid
/// owns its own markup, styles AND gestures — click-to-open, keyboard activation, and the drag-out
/// that hands the file to any app (bevel-cust, moved here verbatim with the rows it belongs to).
/// </summary>
public partial class StackFlyoutView : UserControl
{
    private StackFileViewModel? _dragItem;
    private Point _dragStart;
    private System.Threading.Tasks.Task<IStorageFile?>? _dragFileTask;

    public StackFlyoutView() => InitializeComponent();

    /// <summary>Single click/tap on a cell opens the file (OpenCommand raises Opened, which dismisses
    /// the flyout). A drag gesture suppresses Tapped, so dragging never also opens the file.</summary>
    private void OnStackFileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StackFileViewModel vm)
            vm.OpenCommand.Execute(null);
    }

    /// <summary>Keyboard operability for a cell (bevel-vk4n): Enter/Space opens the file, the same
    /// action as a click/tap.</summary>
    private void OnStackFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if ((sender as Control)?.DataContext is StackFileViewModel vm)
        {
            vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>On press we record the cell and START resolving its IStorageFile, so that by the time
    /// the pointer moves the file is ready and <see cref="DragDrop.DoDragDrop"/> can run synchronously
    /// inside the move handler — awaiting the resolve first would drop the OS drag gesture.</summary>
    private void OnStackFilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed
            && (sender as Control)?.DataContext is StackFileViewModel vm)
        {
            _dragItem = vm;
            _dragStart = e.GetPosition(null);
            _dragFileTask = TopLevel.GetTopLevel(this)?.StorageProvider?.TryGetFileFromPathAsync(ToFileUri(vm.FullPath));
        }
    }

    /// <summary>Once the pointer moves past a small threshold with the button held, start an OS
    /// file-drag carrying the file (bevel-cust) so it can be dropped on Finder or any app. Fires only
    /// when the pre-resolve has completed, keeping DoDragDrop synchronous; otherwise a later move
    /// picks it up.</summary>
    private void OnStackFileMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { _dragItem = null; return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
        if (_dragFileTask is not { IsCompletedSuccessfully: true } resolved) return; // wait a frame for the file
        _dragItem = null;

        var file = resolved.Result;
        if (file is null) return;
        var data = new DataObject();
        data.Set(DataFormats.Files, new[] { file });
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    private static Uri ToFileUri(string path) => new UriBuilder { Scheme = "file", Host = string.Empty, Path = path }.Uri;
}
