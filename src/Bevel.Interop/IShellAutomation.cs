using Bevel.Core.Vfs;

namespace Bevel.Interop;

/// <summary>
/// The canonical command model (08-os-interop.md §3.1). Every inbound automation surface — the
/// hand-written Apple Events object-model resolver (Tier 1), <c>bevelctl</c>, the <c>bevel://</c>
/// URL scheme, and (Linux) DBus <c>FileManager1</c> — converges on this one C# service so they stay
/// behaviourally identical and are testable through a single seam (INT-3). Registered in DI in the
/// core process; single-threaded dispatch onto the UI scheduler for the window-coupled verbs.
///
/// <para>Filesystem verbs (<c>make</c>/<c>delete</c>/<c>duplicate</c>) and the application-object
/// query run entirely against the VFS — no UI, no permissions. The window-coupled verbs
/// (<c>reveal</c>/<c>open</c>/<c>select</c>/<c>set</c>, and the windows/selection query) reach the
/// live file manager through <see cref="IShellSurface"/>.</para>
/// </summary>
public interface IShellAutomation
{
    Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct);
    Task<WindowRef> OpenAsync(VfsPath containerOrFile, OpenOptions opts, CancellationToken ct);
    Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct);
    Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct);
    Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct);
    Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct);
    Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct);
    Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct);
}

/// <summary>
/// The live file-manager operations the command model needs but cannot do from the VFS alone —
/// they act on on-screen windows and selection. Implemented by the file-manager surface (a later
/// M4 increment); faked in tests. Keeping it a narrow seam is what lets the VFS verbs above be
/// fully unit-tested without a running UI.
/// </summary>
public interface IShellSurface
{
    Task<WindowRef> RevealAsync(IReadOnlyList<VfsPath> items, bool newWindow, CancellationToken ct);
    Task<WindowRef> OpenAsync(VfsPath container, ViewMode? view, CancellationToken ct);
    Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct);
    Task<IReadOnlyList<WindowRef>> QueryWindowsAsync(CancellationToken ct);
    Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct);
    Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct);
}

/// <summary>The scriptable <c>application</c> object's location properties (home, desktop, startup
/// disk, trash). Abstracted so tests can point them at a sandbox instead of the real profile.</summary>
public interface IKnownFolders
{
    VfsPath Home { get; }
    VfsPath Desktop { get; }
    VfsPath StartupDisk { get; }
    VfsPath Trash { get; }
}

/// <summary>Real known folders, resolved from the environment (mirrors LocalFsProvider's trash =
/// <c>~/.Trash</c>).</summary>
public sealed class SystemKnownFolders : IKnownFolders
{
    public static readonly SystemKnownFolders Instance = new();

    private static VfsPath File(string p) => new("file", p);

    public VfsPath Home => File(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public VfsPath Desktop => File(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    public VfsPath StartupDisk => File(OperatingSystem.IsWindows()
        ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"
        : "/");
    public VfsPath Trash => File(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"));
}
