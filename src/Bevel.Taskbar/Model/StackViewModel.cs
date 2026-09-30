using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// A taskbar "stack" (bevel-12g): a folder shown as a tray-adjacent button whose flyout lists the
/// folder's most-recent contents — the macOS Downloads-stack equivalent. Discovery is plain
/// <see cref="System.IO"/> (newest-first), icons load off-thread via <see cref="IconLoader"/>, and
/// opening an entry / the folder itself goes through <see cref="IAppEnvironment.LaunchAsync"/>
/// (LaunchServices, so a file opens in its default app). A <see cref="FileSystemWatcher"/> flips the
/// <see cref="HasNew"/> cue when the folder changes; the list itself refreshes lazily on open.
/// </summary>
public sealed class StackViewModel : ObservableObject, IDisposable
{
    /// <summary>Most entries shown in the flyout.</summary>
    public const int MaxItems = 16;

    private readonly IAppEnvironment? _appEnv;
    private readonly IconLoader _icons;
    private readonly PreviewLoader _previews;
    private readonly object _watcherGate = new();
    private FileSystemWatcher? _watcher;
    private Bitmap? _folderIcon;
    private bool _hasNew;
    private bool _disposed;

    public StackViewModel(string folderPath, IAppEnvironment? appEnv, IconLoader icons,
        PreviewLoader? previews = null)
    {
        FolderPath = folderPath;
        _appEnv = appEnv;
        _icons = icons;
        // No thumbnail provider (Windows PAL today, or a test) still gets the grid — every cell just
        // resolves to a larger type icon instead of a content preview.
        _previews = previews ?? new PreviewLoader(icons, thumbnails: null);
        Name = FriendlyName(folderPath);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        _ = LoadFolderIconAsync();
        // NEVER arm the watcher on the UI thread. Default stack is ~/Downloads; on modern macOS
        // Directory.Exists / FSEventStreamCreate on that path can block the calling thread on a
        // Files-and-Folders TCC dialog until the user answers. TaskbarViewModel constructs stacks
        // during surface bring-up, so a sync StartWatching hung ReportReady past ShellHealthMonitor's
        // 15s Starting budget → kill ×3 → Hold StartupStuck (bevel-llfm). Arm off-thread instead.
        _ = Task.Run(StartWatching);
    }

    public string FolderPath { get; }
    public string Name { get; }
    public ObservableCollection<StackFileViewModel> Items { get; } = new();

    /// <summary>The folder's own icon, for the tray-adjacent button.</summary>
    public Bitmap? FolderIcon { get => _folderIcon; private set => SetProperty(ref _folderIcon, value); }

    /// <summary>New-item cue: set when the folder changes, cleared when the flyout is opened.</summary>
    public bool HasNew { get => _hasNew; private set => SetProperty(ref _hasNew, value); }

    public ICommand OpenFolderCommand { get; }

    /// <summary>Non-blocking entry for the click path: kicks the off-thread refresh and returns, so
    /// clicking a stack over a large or slow-backed folder never freezes the taskbar (bevel-gs8l).
    /// The flyout opens optimistically and fills when the enumeration returns.</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>Rebuilds the recent-contents list (newest first) with the directory enumeration + per-
    /// entry stat done OFF the UI thread, marshaling only the item rebuild back. Called when the flyout
    /// opens, so a closed stack costs nothing. Also clears the <see cref="HasNew"/> cue — opening = seen.</summary>
    public async Task RefreshAsync()
    {
        HasNew = false;
        var folder = FolderPath;
        var max = MaxItems;
        var recent = await Task.Run(() => RecentEntries(folder, max)).ConfigureAwait(false);
        _previews.Trim(recent.Select(e => e.Path));   // forget cell images for files that dropped off the list
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Items.Clear();
            foreach (var entry in recent)
            {
                var item = new StackFileViewModel(entry.Path, entry.Stamp, _appEnv, _previews);
                Items.Add(item);
                item.EnsurePreview(); // ~16 items, off-thread — no UI-thread decode cost
            }
        });
    }

    /// <summary>The folder's most-recent entries (files and subfolders), newest first, skipping hidden /
    /// dotfiles. Pure and defensive — a missing/denied folder yields empty. The last-write stamp is
    /// returned alongside the path because this enumeration already reads it to sort; carrying it means
    /// the preview cache can detect a file replaced under the same name without a second stat (and
    /// without one on the UI thread).</summary>
    public static IReadOnlyList<StackEntry> RecentEntries(string folder, int max)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) return Array.Empty<StackEntry>();
            return dir.EnumerateFileSystemInfos()
                .Where(e => !e.Name.StartsWith('.') && !e.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(e => e.LastWriteTimeUtc)
                .Take(max)
                .Select(e => new StackEntry(e.FullName, e.LastWriteTimeUtc.Ticks))
                .ToList();
        }
        catch
        {
            return Array.Empty<StackEntry>();
        }
    }

    private async Task LoadFolderIconAsync()
    {
        var bmp = await _icons.LoadAsync(FolderPath, 16).ConfigureAwait(false);
        if (bmp is not null)
            await Dispatcher.UIThread.InvokeAsync(() => FolderIcon = bmp);
    }

    private async Task OpenFolderAsync()
    {
        if (_appEnv is not null)
        {
            try { await _appEnv.LaunchAsync(FolderPath); }
            catch (Exception ex) { TaskbarLog.Swallowed("Stack.OpenFolder", ex); }
        }
    }

    private void StartWatching()
    {
        try
        {
            if (_disposed || !Directory.Exists(FolderPath)) return;
            var watcher = new FileSystemWatcher(FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Created += OnFolderChanged;
            watcher.Renamed += OnFolderChanged;
            watcher.Changed += OnFolderChanged;
            lock (_watcherGate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }
                _watcher = watcher;
            }
        }
        catch (Exception ex)
        {
            TaskbarLog.Swallowed("Stack.Watch", ex); // watching is best-effort; the flyout still refreshes on open
        }
    }

    private void OnFolderChanged(object? sender, FileSystemEventArgs e)
    {
        // Coalesce a change storm: once the "new items" cue is set, further events add nothing, so
        // don't flood the dispatcher with a Post per filesystem event (ce-review). The read is a
        // cheap racy check — a redundant Post at worst, never a missed cue (Post re-sets true).
        if (!HasNew) Dispatcher.UIThread.Post(() => HasNew = true);
    }

    private static string FriendlyName(string folderPath)
    {
        var name = new DirectoryInfo(folderPath.TrimEnd('/', '\\')).Name;
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    public void Dispose()
    {
        _disposed = true;
        FileSystemWatcher? watcher;
        lock (_watcherGate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher is null) return;
        watcher.Created -= OnFolderChanged;
        watcher.Renamed -= OnFolderChanged;
        watcher.Changed -= OnFolderChanged;
        watcher.Dispose();
    }
}

/// <summary>One cell in a stack's grid flyout: a file or subfolder that opens in its default handler
/// (bevel-12g). The cell image is a real content preview where the platform can decode one and the
/// file's type icon at cell size otherwise (bevel-9elh); it loads lazily off-thread, mirroring
/// <see cref="ProgramItemViewModel"/>.</summary>
/// <summary>One entry of a stack's recent-contents listing: its full path plus the last-write tick count
/// the enumeration already read in order to sort. The stamp travels with the path so the preview cache can
/// tell "same file" from "replaced under the same name" without a second stat — and, since the cell load is
/// kicked off from inside a UI-thread dispatch, without ever stat-ing on the UI thread.</summary>
public readonly record struct StackEntry(string Path, long Stamp);

public sealed class StackFileViewModel : ObservableObject
{
    private readonly IAppEnvironment? _appEnv;
    private readonly PreviewLoader _previews;
    private readonly string _path;
    private readonly long _stamp;
    private Bitmap? _previewSource;
    private bool _hasContentPreview;
    private bool _previewRequested;

    public StackFileViewModel(string path, long stamp, IAppEnvironment? appEnv, PreviewLoader previews)
    {
        _path = path;
        _stamp = stamp;
        _appEnv = appEnv;
        _previews = previews;
        Name = Path.GetFileName(path.TrimEnd('/', '\\'));
        OpenCommand = new AsyncRelayCommand(OpenAsync);
    }

    public string Name { get; }
    /// <summary>Absolute path — used to drag the file out of the stack flyout to other apps (bevel-cust).</summary>
    public string FullPath => _path;

    /// <summary>The cell image: the file's content preview, or its type icon at cell size.</summary>
    public Bitmap? PreviewSource { get => _previewSource; private set => SetProperty(ref _previewSource, value); }

    /// <summary>True only when <see cref="PreviewSource"/> is a REAL content preview (a decoded photo,
    /// a PDF page) rather than a type icon — the grid frames the two differently, so a cell never
    /// passes a generic icon off as a preview of the file's contents.</summary>
    public bool HasContentPreview { get => _hasContentPreview; private set => SetProperty(ref _hasContentPreview, value); }

    public ICommand OpenCommand { get; }

    /// <summary>Raised after opening so the host can dismiss the flyout.</summary>
    public event Action? Opened;

    public void EnsurePreview()
    {
        if (_previewRequested) return;
        _previewRequested = true;
        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        var cell = await _previews.LoadAsync(_path, _stamp).ConfigureAwait(false);
        if (cell.Image is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            PreviewSource = cell.Image;
            HasContentPreview = cell.IsContentPreview;
        });
    }

    private async Task OpenAsync()
    {
        if (_appEnv is not null)
        {
            try { await _appEnv.LaunchAsync(_path); }
            catch (Exception ex) { TaskbarLog.Swallowed("Stack.Open", ex); }
        }
        Opened?.Invoke();
    }
}

/// <summary>The set of configured taskbar stacks (bevel-12g), built from the folder paths in settings.</summary>
public sealed class StacksViewModel : IDisposable
{
    public StacksViewModel(IEnumerable<string> folders, IAppEnvironment? appEnv, IIconProvider? icons,
        IThumbnailProvider? thumbnails = null)
    {
        var loader = new IconLoader(icons);
        foreach (var folder in folders)
            // One PreviewLoader per stack: its cache is trimmed to that folder's live entries, so
            // stacks never evict each other's cell images.
            Stacks.Add(new StackViewModel(folder, appEnv, loader, new PreviewLoader(loader, thumbnails)));
    }

    public ObservableCollection<StackViewModel> Stacks { get; } = new();

    public void Dispose()
    {
        foreach (var s in Stacks) s.Dispose();
    }
}
