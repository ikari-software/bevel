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
    private FileSystemWatcher? _watcher;
    private Bitmap? _folderIcon;
    private bool _hasNew;

    public StackViewModel(string folderPath, IAppEnvironment? appEnv, IconLoader icons)
    {
        FolderPath = folderPath;
        _appEnv = appEnv;
        _icons = icons;
        Name = FriendlyName(folderPath);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        _ = LoadFolderIconAsync();
        StartWatching();
    }

    public string FolderPath { get; }
    public string Name { get; }
    public ObservableCollection<StackFileViewModel> Items { get; } = new();

    /// <summary>The folder's own icon, for the tray-adjacent button.</summary>
    public Bitmap? FolderIcon { get => _folderIcon; private set => SetProperty(ref _folderIcon, value); }

    /// <summary>New-item cue: set when the folder changes, cleared when the flyout is opened.</summary>
    public bool HasNew { get => _hasNew; private set => SetProperty(ref _hasNew, value); }

    public ICommand OpenFolderCommand { get; }

    /// <summary>Rebuilds the recent-contents list (newest first). Called when the flyout opens, so a
    /// closed stack costs nothing. Also clears the <see cref="HasNew"/> cue — opening = seen.</summary>
    public void Refresh()
    {
        HasNew = false;
        var recent = RecentEntries(FolderPath, MaxItems);
        Items.Clear();
        foreach (var path in recent)
        {
            var item = new StackFileViewModel(path, _appEnv, _icons);
            Items.Add(item);
            item.EnsureIcon(); // ~16 items, off-thread — no UI-thread render cost
        }
    }

    /// <summary>The folder's most-recent entries (files and subfolders) as full paths, newest first,
    /// skipping hidden / dotfiles. Pure and defensive — a missing/denied folder yields empty.</summary>
    public static IReadOnlyList<string> RecentEntries(string folder, int max)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) return Array.Empty<string>();
            return dir.EnumerateFileSystemInfos()
                .Where(e => !e.Name.StartsWith('.') && !e.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(e => e.LastWriteTimeUtc)
                .Take(max)
                .Select(e => e.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
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
            if (!Directory.Exists(FolderPath)) return;
            _watcher = new FileSystemWatcher(FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
        }
        catch (Exception ex)
        {
            TaskbarLog.Swallowed("Stack.Watch", ex); // watching is best-effort; the flyout still refreshes on open
        }
    }

    private void OnFolderChanged(object? sender, FileSystemEventArgs e)
        => Dispatcher.UIThread.Post(() => HasNew = true);

    private static string FriendlyName(string folderPath)
    {
        var name = new DirectoryInfo(folderPath.TrimEnd('/', '\\')).Name;
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.Created -= OnFolderChanged;
        _watcher.Renamed -= OnFolderChanged;
        _watcher.Changed -= OnFolderChanged;
        _watcher.Dispose();
        _watcher = null;
    }
}

/// <summary>One entry in a stack's flyout: a file or subfolder that opens in its default handler
/// (bevel-12g). Icon loads lazily off-thread, mirroring <see cref="ProgramItemViewModel"/>.</summary>
public sealed class StackFileViewModel : ObservableObject
{
    private readonly IAppEnvironment? _appEnv;
    private readonly IconLoader _icons;
    private readonly string _path;
    private Bitmap? _iconSource;
    private bool _iconRequested;

    public StackFileViewModel(string path, IAppEnvironment? appEnv, IconLoader icons)
    {
        _path = path;
        _appEnv = appEnv;
        _icons = icons;
        Name = Path.GetFileName(path.TrimEnd('/', '\\'));
        OpenCommand = new AsyncRelayCommand(OpenAsync);
    }

    public string Name { get; }
    public Bitmap? IconSource { get => _iconSource; private set => SetProperty(ref _iconSource, value); }
    public ICommand OpenCommand { get; }

    /// <summary>Raised after opening so the host can dismiss the flyout.</summary>
    public event Action? Opened;

    public void EnsureIcon()
    {
        if (_iconRequested) return;
        _iconRequested = true;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        var bmp = await _icons.LoadAsync(_path, 16).ConfigureAwait(false);
        if (bmp is null) return;
        await Dispatcher.UIThread.InvokeAsync(() => IconSource = bmp);
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
    public StacksViewModel(IEnumerable<string> folders, IAppEnvironment? appEnv, IIconProvider? icons)
    {
        var loader = new IconLoader(icons);
        foreach (var folder in folders)
            Stacks.Add(new StackViewModel(folder, appEnv, loader));
    }

    public ObservableCollection<StackViewModel> Stacks { get; } = new();

    public void Dispose()
    {
        foreach (var s in Stacks) s.Dispose();
    }
}
