using Bevel.Core.Vfs;

namespace Bevel.Interop;

/// <summary>
/// The one implementation of the canonical command model (08-os-interop.md §3.1). Filesystem verbs
/// run against the <see cref="VfsRoot"/>; the window-coupled verbs delegate to an
/// <see cref="IShellSurface"/>. This is the resolver's execution target — the AE handler, CLI, and
/// URL scheme all call these methods, never the filesystem directly (INT-3).
/// </summary>
public sealed class ShellAutomation : IShellAutomation
{
    // Enumerate everything when computing collision-free names / copying, so a hidden sibling can't
    // cause a name clash or be silently dropped from a folder duplicate.
    private static readonly EnumerateOptions AllItems = new() { IncludeHidden = true, IncludeSystem = true };

    private readonly VfsRoot _vfs;
    private readonly IShellSurface _surface;
    private readonly IKnownFolders _known;
    private readonly IProgramSurface? _programs;
    private readonly IShellLifecycle? _lifecycle;

    public ShellAutomation(VfsRoot vfs, IShellSurface surface, IKnownFolders? knownFolders = null,
        IProgramSurface? programs = null, IShellLifecycle? lifecycle = null)
    {
        _vfs = vfs;
        _surface = surface;
        _known = knownFolders ?? SystemKnownFolders.Instance;
        _programs = programs;
        _lifecycle = lifecycle;
    }

    // ── Window-coupled verbs (delegate to the live surface) ──────────────────────────────────

    public async Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct)
    {
        var window = await _surface.RevealAsync(items, opts.NewWindow, ct);
        return new RevealResult(window, items.Count);
    }

    public Task<WindowRef> OpenAsync(VfsPath containerOrFile, OpenOptions opts, CancellationToken ct)
        => _surface.OpenAsync(containerOrFile, opts.View, ct);

    public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct)
        => _surface.SelectAsync(window, items, ct);

    public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct)
        => _surface.SetAsync(target, prop, value, ct);

    public Task LaunchAsync(string appId, CancellationToken ct)
        => (_programs ?? throw new AutomationException("program launching is not available")).LaunchAsync(appId, ct);

    public Task QuitAsync(CancellationToken ct)
        => (_lifecycle ?? throw new AutomationException("shutting down is not available here")).QuitAsync(ct);

    // ── Filesystem verbs (run against the VFS) ───────────────────────────────────────────────

    public async Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct)
    {
        var mutator = await MutatorForAsync(parent, ct);
        var existing = await NamesInAsync(parent, ct);

        if (kind == NewItemKind.Folder)
        {
            var folderName = FinderNaming.UniqueName(name ?? "untitled folder", isFolder: true, existing.Contains);
            return await mutator.CreateFolderAsync(parent, folderName, ct);
        }

        var fileName = FinderNaming.UniqueName(name ?? "untitled", isFolder: false, existing.Contains);
        var filePath = VfsPath.Combine(parent, fileName);
        await using var _ = await mutator.OpenWriteAsync(filePath, ct);   // touch → empty file
        return filePath;
    }

    public async Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var mutator = await MutatorForAsync(item.Parent, ct);
            await mutator.DeleteAsync(item, toTrash: mode == DeleteMode.Trash, ct);
        }
    }

    public async Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct)
    {
        var results = new List<VfsPath>(items.Count);
        foreach (var item in items)
        {
            var node = await _vfs.ResolveAsync(item, ct);
            var container = target ?? item.Parent;
            var existing = await NamesInAsync(container, ct);
            var newName = FinderNaming.DuplicateName(item.FileName, node.Kind == VfsNodeKind.Folder, existing.Contains);
            results.Add(await CopyNodeAsync(node, container, newName, ct));
        }
        return results;
    }

    public async Task<IReadOnlyList<VfsPath>> MoveAsync(IReadOnlyList<VfsPath> items, VfsPath destination, CancellationToken ct)
    {
        var results = new List<VfsPath>(items.Count);
        foreach (var item in items)
        {
            var mutator = await MutatorForAsync(item.Parent, ct);
            results.Add(await mutator.MoveAsync(item, destination, ct));
        }
        return results;
    }

    public async Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct)
    {
        var snapshot = new BevelStateSnapshot();

        if (query.HasFlag(AutomationQuery.Application))
            snapshot = snapshot with
            {
                Version = typeof(ShellAutomation).Assembly.GetName().Version?.ToString(),
                Home = _known.Home,
                Desktop = _known.Desktop,
                StartupDisk = _known.StartupDisk,
                Trash = _known.Trash,
            };

        if (query.HasFlag(AutomationQuery.Windows))
            snapshot = snapshot with { Windows = await _surface.QueryWindowsAsync(ct) };

        if (query.HasFlag(AutomationQuery.Selection))
            snapshot = snapshot with { Selection = await _surface.QuerySelectionAsync(null, ct) };

        if (query.HasFlag(AutomationQuery.Programs) && _programs is not null)
            snapshot = snapshot with { Programs = await _programs.ListProgramsAsync(ct) };

        return snapshot;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Recursively copies a node into <paramref name="destContainer"/> under
    /// <paramref name="destName"/>; children keep their own leaf names.</summary>
    private async Task<VfsPath> CopyNodeAsync(IVfsNode node, VfsPath destContainer, string destName, CancellationToken ct)
    {
        var mutator = await MutatorForAsync(destContainer, ct);

        if (node.Kind == VfsNodeKind.Folder)
        {
            var destFolder = await mutator.CreateFolderAsync(destContainer, destName, ct);
            await foreach (var child in _vfs.EnumerateAsync(node.Path, AllItems, ct))
                await CopyNodeAsync(child, destFolder, child.Path.FileName, ct);
            return destFolder;
        }

        var destPath = VfsPath.Combine(destContainer, destName);
        await using var src = await _vfs.OpenReadAsync(node.Path, ct);
        await using var dst = await mutator.OpenWriteAsync(destPath, ct);
        await src.CopyToAsync(dst, ct);
        return destPath;
    }

    private async Task<IVfsMutator> MutatorForAsync(VfsPath container, CancellationToken ct)
    {
        var mutator = await _vfs.GetProvider(container).GetMutatorAsync(container, ct);
        return mutator ?? throw new AutomationException($"'{container}' is read-only; the command cannot be performed there.");
    }

    private async Task<HashSet<string>> NamesInAsync(VfsPath container, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var child in _vfs.EnumerateAsync(container, AllItems, ct))
            names.Add(child.Path.FileName);
        return names;
    }
}
