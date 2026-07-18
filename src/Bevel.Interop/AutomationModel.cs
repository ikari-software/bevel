using Bevel.Core.Vfs;

namespace Bevel.Interop;

// ── Command-model value types (08-os-interop.md §2.1.2 verbs + §3.1 signatures) ──────────────
//
// BevelPath in the spec IS the VFS path type (VfsPath, from 06-file-manager.md), so the command
// model speaks VfsPath directly rather than wrapping it in a parallel type.

/// <summary>What <c>make</c> creates. v1 mirrors Finder's <c>make new folder</c> (+ empty file).</summary>
public enum NewItemKind { Folder, File }

/// <summary>How <c>delete</c> removes items. Finder's <c>delete</c> is Trash; Permanent is the
/// shift-delete / <c>rm</c> path.</summary>
public enum DeleteMode { Trash, Permanent }

/// <summary>Finder window view. <c>column</c> maps to <see cref="Details"/> in v1 (documented deviation).</summary>
public enum ViewMode { Icons, List, Details }

/// <summary>What <see cref="IShellAutomation.QueryAsync"/> should gather. Flags so one round-trip can
/// fetch the application object, open windows, and the active selection together.</summary>
[Flags]
public enum AutomationQuery { None = 0, Application = 1, Windows = 2, Selection = 4 }

/// <summary>Settable window/view properties for <c>set</c> (08-os-interop.md §2.1.2 window class).</summary>
public enum AutomationProperty { Selection, CurrentView, Bounds, ToolbarVisible, SidebarWidth }

/// <summary>Opaque handle to a live file-manager window, minted by the shell surface.</summary>
public readonly record struct WindowRef(int Id);

/// <summary>Options for <c>reveal</c>.</summary>
public sealed record RevealOptions(bool NewWindow = false);

/// <summary>Options for <c>open</c>.</summary>
public sealed record OpenOptions(ViewMode? View = null);

/// <summary>Result of <c>reveal</c>: the window showing the container + how many items got selected.</summary>
public sealed record RevealResult(WindowRef Window, int SelectedCount);

/// <summary>The object a <c>set</c> targets (a specific window, or the application when null).</summary>
public sealed record AutomationTarget(WindowRef? Window = null);

/// <summary>A read-only snapshot answering a <see cref="AutomationQuery"/>: the scriptable
/// <c>application</c> object's core properties plus, when asked, open windows and the selection.</summary>
public sealed record BevelStateSnapshot
{
    public string? Version { get; init; }
    public VfsPath? Home { get; init; }
    public VfsPath? Desktop { get; init; }
    public VfsPath? StartupDisk { get; init; }
    public VfsPath? Trash { get; init; }
    public IReadOnlyList<WindowRef> Windows { get; init; } = Array.Empty<WindowRef>();
    public IReadOnlyList<VfsPath> Selection { get; init; } = Array.Empty<VfsPath>();
}

/// <summary>A command-model fault (read-only container, missing target, unsupported verb). The
/// Apple Events handler maps this to <c>errAEEventNotHandled</c> with the message, rather than
/// propagating (08-os-interop.md §2.1.2 isolation mitigation).</summary>
public sealed class AutomationException : Exception
{
    public AutomationException(string message) : base(message) { }
}
