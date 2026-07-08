using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Bevel.FileManager.Navigation;

namespace Bevel.FileManager;

/// <summary>
/// The command seam between the file-manager UI and <see cref="FileOperationService"/>
/// (bevel-o2t). Owns navigation history, the current directory, the active selection, and a
/// cut/copy clipboard; translates high-level actions (copy/cut/paste/delete/rename/new-folder)
/// into <see cref="FileOperationService"/> requests or VFS mutations.
///
/// Both the window (today) and the future M4 Apple-Events dispatcher drive the manager through
/// this one object, so any action a user can take an agent can take too. It has no Avalonia
/// dependency and is unit-testable in isolation — the view pushes selection into it and reacts
/// to <see cref="CurrentDirectoryChanged"/> / <see cref="NavigationStateChanged"/>.
/// </summary>
public sealed class FileManagerController
{
    private readonly VfsRoot _vfs;
    private readonly FileOperationService _fileOps;
    private readonly NavigationStack _nav = new();

    private IReadOnlyList<VfsPath> _selection = Array.Empty<VfsPath>();
    private (IReadOnlyList<VfsPath> Paths, bool IsCut)? _clipboard;

    public FileManagerController(VfsRoot vfs, FileOperationService fileOps)
    {
        _vfs = vfs;
        _fileOps = fileOps;
    }

    // ── State ──────────────────────────────────────────────────────────
    public VfsPath CurrentDirectory => _nav.Current;
    public bool CanGoBack => _nav.CanGoBack;
    public bool CanGoForward => _nav.CanGoForward;

    /// <summary>Back-history paths, most-recent-first — for a History dropdown's "back" section.</summary>
    public IReadOnlyList<VfsPath> BackHistory => _nav.Back;

    /// <summary>Forward-history paths, nearest-next-first — for a History dropdown's "forward" section.</summary>
    public IReadOnlyList<VfsPath> ForwardHistory => _nav.Forward;

    /// <summary>
    /// The full navigation history as labeled, indexed entries (oldest first) — pass an entry's
    /// <see cref="HistoryEntry.Index"/> to <see cref="JumpToHistory"/> to build a History dropdown
    /// menu (Toolbar History button / Back-chevron flyout, Go menu).
    /// </summary>
    public IReadOnlyList<HistoryEntry> HistoryMenu
        => _nav.Entries.Select((p, i) => new HistoryEntry(i, p, HistoryEntry.LabelFor(p))).ToArray();

    /// <summary>The absolute index of the current directory within <see cref="HistoryMenu"/>.</summary>
    public int HistoryPosition => _nav.Position;

    public IReadOnlyList<VfsPath> Selection => _selection;
    public bool HasClipboard => _clipboard is { Paths.Count: > 0 };
    public bool CanUndo => _fileOps.Undo.CanUndo;

    /// <summary>Live progress of the running operation — the view subscribes for its progress dialog.</summary>
    public IObservable<FileOpProgress> Progress => _fileOps.Progress;

    /// <summary>
    /// Optional hook the view installs to run a request through a progress UI. It is handed the
    /// request and the inner execute delegate (which runs it on the service under a supplied
    /// token); when null, requests run directly. This lets the ProgressDialog live in the view
    /// while all mutations still funnel through this one seam.
    /// </summary>
    public Func<FileOpRequest, Func<CancellationToken, Task<FileOpResult>>, Task<FileOpResult>>? OperationRunner { get; set; }

    // ── Events ─────────────────────────────────────────────────────────
    /// <summary>The current directory changed (navigate/refresh) — the view should (re)load it.</summary>
    public event Action<VfsPath>? CurrentDirectoryChanged;

    /// <summary>Back/Forward availability changed.</summary>
    public event Action? NavigationStateChanged;

    /// <summary>A mutation finished (success or partial). For observers — status bar, AE logs.</summary>
    public event Action<FileOpResult>? OperationCompleted;

    // ── Selection (pushed from the view, or set directly by an agent) ───
    public void SetSelection(IReadOnlyList<VfsPath> paths) => _selection = paths ?? Array.Empty<VfsPath>();

    // ── Navigation ─────────────────────────────────────────────────────
    public void NavigateTo(VfsPath path)
    {
        // Navigating to where we already are is a no-op. Re-firing Arrived would start a second,
        // racing LoadDirectory of the same folder — which streams a duplicate listing. (This
        // happens on startup: SetController navigates Home, then the window factory navigates to
        // the same Home.) Use Refresh() to force a genuine reload.
        if (_nav.Count > 0 && _nav.Current == path) return;
        _nav.Push(path);
        Arrived(path);
    }

    public void GoBack() { if (_nav.GoBack() is { } p) Arrived(p); }
    public void GoForward() { if (_nav.GoForward() is { } p) Arrived(p); }

    // GoUp returns the parent without mutating the stack; record it as forward history.
    public void GoUp() { if (_nav.GoUp() is { } p) { _nav.Push(p); Arrived(p); } }

    /// <summary>
    /// Jump directly to an entry in <see cref="HistoryMenu"/> (by its absolute
    /// <see cref="HistoryEntry.Index"/>) — repositions the history pointer WITHOUT pushing a new
    /// entry or truncating forward history, then fires the same arrival events as
    /// <see cref="GoBack"/>/<see cref="GoForward"/>. No-op if <paramref name="index"/> is out of range.
    /// </summary>
    public void JumpToHistory(int index) { if (_nav.GoTo(index) is { } p) Arrived(p); }

    /// <summary>Re-raise the current directory so the view reloads it (no history change).</summary>
    public void Refresh() => CurrentDirectoryChanged?.Invoke(_nav.Current);

    private void Arrived(VfsPath path)
    {
        NavigationStateChanged?.Invoke();
        CurrentDirectoryChanged?.Invoke(path);
    }

    // ── Clipboard ──────────────────────────────────────────────────────
    public void CopyToClipboard(IReadOnlyList<VfsPath> paths) => _clipboard = (paths.ToArray(), false);
    public void CutToClipboard(IReadOnlyList<VfsPath> paths) => _clipboard = (paths.ToArray(), true);
    public void CopySelectionToClipboard() => CopyToClipboard(_selection);
    public void CutSelectionToClipboard() => CutToClipboard(_selection);

    // ── Mutations (delegate to FileOperationService) ───────────────────
    public Task<FileOpResult> CopyAsync(IReadOnlyList<VfsPath> sources, VfsPath destination, CancellationToken ct = default)
        => RunAsync(new CopyRequest { Timestamp = DateTimeOffset.UtcNow, Sources = sources, Destination = destination }, ct);

    public Task<FileOpResult> MoveAsync(IReadOnlyList<VfsPath> sources, VfsPath destination, CancellationToken ct = default)
        => RunAsync(new MoveRequest { Timestamp = DateTimeOffset.UtcNow, Sources = sources, Destination = destination }, ct);

    public Task<FileOpResult> DeleteAsync(IReadOnlyList<VfsPath> paths, bool toTrash, CancellationToken ct = default)
        => RunAsync(new DeleteRequest { Timestamp = DateTimeOffset.UtcNow, Paths = paths, ToTrash = toTrash }, ct);

    public Task<FileOpResult> RenameAsync(VfsPath path, string newName, CancellationToken ct = default)
        => RunAsync(new RenameRequest { Timestamp = DateTimeOffset.UtcNow, Path = path, NewName = newName }, ct);

    public async Task<FileOpResult> UndoAsync(CancellationToken ct = default)
    {
        var result = await _fileOps.UndoAsync(ct);
        OperationCompleted?.Invoke(result);
        return result;
    }

    /// <summary>Delete the current selection; null when nothing is selected.</summary>
    public Task<FileOpResult>? DeleteSelectionAsync(bool toTrash, CancellationToken ct = default)
        => _selection.Count == 0 ? null : DeleteAsync(_selection, toTrash, ct);

    /// <summary>
    /// Paste the clipboard into the current directory: copy, or move for a cut (which is then
    /// consumed). Returns null when the clipboard is empty.
    /// </summary>
    public async Task<FileOpResult?> PasteAsync(CancellationToken ct = default)
    {
        if (_clipboard is not { Paths.Count: > 0 } clip) return null;
        var result = clip.IsCut
            ? await MoveAsync(clip.Paths, CurrentDirectory, ct)
            : await CopyAsync(clip.Paths, CurrentDirectory, ct);
        if (clip.IsCut) _clipboard = null;
        return result;
    }

    /// <summary>
    /// Create a new folder in <paramref name="directory"/> (default: current), picking a free
    /// name like Explorer ("New Folder", "New Folder (2)", …). Returns its path, or null when
    /// the target is read-only. Undo is not yet wired (see bevel-o2t.2 / follow-up).
    /// </summary>
    public async Task<VfsPath?> NewFolderAsync(VfsPath? directory = null, string baseName = "New Folder", CancellationToken ct = default)
    {
        var dir = directory ?? CurrentDirectory;
        var mutator = await _vfs.GetProvider(dir).GetMutatorAsync(dir, ct);
        if (mutator is null) return null;

        // CreateFolderAsync is idempotent (Directory.CreateDirectory), so choose a free name
        // ourselves rather than silently reusing an existing folder.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var child in _vfs.EnumerateAsync(dir, new EnumerateOptions(), ct))
                existing.Add(child.DisplayName);
        }
        catch { /* best-effort — fall back to the base name */ }

        var name = baseName;
        for (var n = 2; existing.Contains(name); n++)
            name = $"{baseName} ({n})";

        return await mutator.CreateFolderAsync(dir, name, ct);
    }

    private async Task<FileOpResult> RunAsync(FileOpRequest request, CancellationToken ct)
    {
        Func<CancellationToken, Task<FileOpResult>> exec = c => _fileOps.ExecuteAsync(request, c);
        var result = OperationRunner is { } run ? await run(request, exec) : await exec(ct);
        OperationCompleted?.Invoke(result);
        return result;
    }
}
