using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// ViewModel for the Win2000 "Search for Files or Folders" pane.
/// Streaming walk from look-in root using VFS enumeration, glob/substring name match,
/// cancellable. Results render in the normal item view with an In Folder column.
/// Spec: FM-170, FM-171.
/// </summary>
public sealed class SearchPaneViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly VfsRoot _vfsRoot;
    private CancellationTokenSource? _searchCts;
    private string _nameContains = string.Empty;
    private string _containingText = string.Empty;
    private string _lookInPath = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSearching;
    private string _currentLookIn = string.Empty;

    public SearchPaneViewModel(VfsRoot vfsRoot)
    {
        _vfsRoot = vfsRoot;
        Results = new ObservableCollection<SearchResultItem>();
    }

    /// <summary>Event raised when the user double-clicks a result to navigate.</summary>
    public event EventHandler<VfsPath>? NavigateToRequested;

    public string NameContains
    {
        get => _nameContains;
        set => SetField(ref _nameContains, value);
    }

    public string ContainingText
    {
        get => _containingText;
        set => SetField(ref _containingText, value);
    }

    public string LookInPath
    {
        get => _lookInPath;
        set => SetField(ref _lookInPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetField(ref _isSearching, value);
    }

    public string CurrentLookIn
    {
        get => _currentLookIn;
        set => SetField(ref _currentLookIn, value);
    }

    public ObservableCollection<SearchResultItem> Results { get; }

    /// <summary>Start a search. Streaming walk from look-in root, glob/substring match.</summary>
    public void StartSearch()
    {
        if (string.IsNullOrWhiteSpace(LookInPath))
        {
            StatusText = "Please specify a location to search.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NameContains) && string.IsNullOrWhiteSpace(ContainingText))
        {
            StatusText = "Please specify at least a name or text to search for.";
            return;
        }

        StopSearch();
        Results.Clear();

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        StatusText = "Searching...";

        // Fire-and-forget the async walk; results stream into the ObservableCollection.
        // WalkAsync OWNS the CTS lifetime and disposes it only when the walk ends, so the
        // token is never disposed while the enumeration is still using it (bevel-u0z).
        _ = WalkAsync(cts);
    }

    /// <summary>Cancel the current search.</summary>
    public void StopSearch()
    {
        // Cancel the in-flight walk but do NOT dispose its CTS here — the running
        // WalkAsync still holds the token and will dispose the CTS itself. Disposing now
        // would throw ObjectDisposedException inside the enumeration (bevel-u0z).
        var cts = Interlocked.Exchange(ref _searchCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* walk already completed and disposed it */ }
        }

        IsSearching = false;
        StatusText = $"Search cancelled. {Results.Count} result(s) found.";
    }

    /// <summary>Navigate to a result in the main item view.</summary>
    public void NavigateToResult(SearchResultItem item)
    {
        NavigateToRequested?.Invoke(this, item.Path);
    }

    private async Task WalkAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var matchCount = 0;
        var matchName = !string.IsNullOrWhiteSpace(NameContains);
        var namePattern = matchName ? NameContains.Trim() : string.Empty;
        var matchText = !string.IsNullOrWhiteSpace(ContainingText);
        var textPattern = matchText ? ContainingText.Trim() : string.Empty;

        VfsPath lookIn;
        try
        {
            lookIn = ParseLookInPath(LookInPath);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Invalid look-in path: {ex.Message}";
                IsSearching = false;
            });
            return;
        }

        // Depth-first recursive walk using a stack to avoid async-foreach overhead.
        var stack = new Stack<VfsPath>();
        stack.Push(lookIn);

        try
        {
            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                var current = stack.Pop();

                // Update status on UI thread
                var displayPath = FormatDisplayPath(current);
                Dispatcher.UIThread.Post(() => CurrentLookIn = displayPath);

                var options = new EnumerateOptions
                {
                    IncludeHidden = true,
                    IncludeSystem = false,
                };

                try
                {
                    await foreach (var node in _vfsRoot.EnumerateAsync(current, options, ct))
                    {
                        ct.ThrowIfCancellationRequested();

                        if (node.Kind == VfsNodeKind.Folder)
                        {
                            stack.Push(node.Path);
                        }

                        // Name match — substring (case-insensitive)
                        if (matchName && !NameMatches(node.DisplayName, namePattern))
                            continue;

                        // Text content match — only for files
                        if (matchText)
                        {
                            if (node.Kind != VfsNodeKind.File) continue;

                            if (!await ContentMatchesAsync(node, textPattern, ct))
                                continue;
                        }

                        matchCount++;
                        var inFolder = FormatInFolder(node.Path, current);
                        var item = new SearchResultItem(node, inFolder);

                        // Re-check the walk's own token inside the UI post: a result queued
                        // just before this search was cancelled must not land in the next
                        // search's freshly-cleared list (review #15).
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ct.IsCancellationRequested)
                                Results.Add(item);
                        });

                        // Yield control periodically so the UI stays responsive
                        if (matchCount % 50 == 0)
                            await Task.Yield();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Skip inaccessible directories
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            // The walk has finished using the token; clear the field if it still points
            // here, then dispose the CTS we own.
            Interlocked.CompareExchange(ref _searchCts, null, cts);
            cts.Dispose();

            Dispatcher.UIThread.Post(() =>
            {
                IsSearching = false;
                StatusText = ct.IsCancellationRequested
                    ? $"Search cancelled. {Results.Count} result(s) found."
                    : $"Done. {Results.Count} result(s) found.";
            });
        }
    }

    /// <summary>
    /// Substring match (case-insensitive). Treats * as wildcard for prefix/suffix.
    /// </summary>
    private static bool NameMatches(string displayName, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;

        // Simple glob: support leading/trailing * wildcards
        var trimmed = pattern.Trim('*');
        if (trimmed.Length == 0) return true;

        return displayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ContentMatchesAsync(IVfsNode node, string text, CancellationToken ct)
    {
        // Skip large files (> 10 MB) to avoid blocking.
        if (node.Size is > 10 * 1024 * 1024) return false;

        try
        {
            // Read the bytes through the provider's OpenReadAsync so content search works
            // for every scheme (zip, net, …), not just local files (bevel-5ya).
            await using var stream = await _vfsRoot.OpenReadAsync(node.Path, ct);
            using var reader = new StreamReader(stream);

            var buffer = new char[8192];
            int charsRead;
            while ((charsRead = await reader.ReadAsync(buffer, ct)) > 0)
            {
                var segment = new string(buffer, 0, charsRead);
                if (segment.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // honor cancellation promptly
        }
        catch
        {
            // Unreadable file / provider error — treat as no match.
        }

        return false;
    }

    private static VfsPath ParseLookInPath(string lookIn)
    {
        // Accept both canonical VFS paths and bare filesystem paths, through the one
        // parser in VfsPath (no bespoke Uri handling that would URL-decode the value).
        if (VfsPath.TryParse(lookIn, out var parsed))
            return parsed;

        // Bare path — treat as local filesystem.
        return new VfsPath("file", Path.GetFullPath(lookIn));
    }

    private static string FormatInFolder(VfsPath nodePath, VfsPath lookIn)
    {
        // Show the relative path from look-in root to the node's parent
        var nodeParent = nodePath.ParentValue;
        if (string.IsNullOrEmpty(nodeParent))
            return nodePath.FileName;

        // For the file scheme, both Value parts are absolute paths
        if (lookIn.Scheme == "file" && nodePath.Scheme == "file")
        {
            var lookInVal = lookIn.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (nodeParent.StartsWith(lookInVal, StringComparison.OrdinalIgnoreCase))
            {
                var rel = nodeParent[lookInVal.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.IsNullOrEmpty(rel) ? lookIn.FileName : rel;
            }
        }

        return nodeParent;
    }

    private static string FormatDisplayPath(VfsPath path)
    {
        if (path.Scheme == "file")
            return path.Value;
        return path.ToString();
    }

    public void Dispose()
    {
        // Only cancel — an in-flight WalkAsync owns and disposes its CTS. Disposing here
        // could race with a walk still using the token.
        var cts = _searchCts;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* walk already completed */ }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// Presentation item for a search result. Wraps an IVfsNode with an "In Folder" display column.
/// </summary>
public sealed class SearchResultItem : INotifyPropertyChanged
{
    private readonly IVfsNode _node;

    public SearchResultItem(IVfsNode node, string inFolderDisplay)
    {
        _node = node;
        InFolderDisplay = inFolderDisplay;
    }

    public VfsPath Path => _node.Path;
    public string DisplayName => _node.DisplayName;
    public VfsNodeKind Kind => _node.Kind;
    public bool IsFolder => _node.Kind is VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot;
    public bool IsFile => _node.Kind is VfsNodeKind.File;
    public long? Size => _node.Size;
    public DateTimeOffset? Modified => _node.Modified;
    public string TypeDescription => _node.TypeDescription;
    public IconKey IconKey => _node.IconKey;
    public string InFolderDisplay { get; }

    public string SizeDisplay => Size switch
    {
        null => "",
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        < 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024):0} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):0} GB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
