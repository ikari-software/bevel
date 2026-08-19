using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;

namespace Bevel.App;

/// <summary>
/// Spawns a new <c>--role=explorer</c> process. The seam that lets the persistent-host
/// <see cref="SpawningShellSurface"/> be unit-tested with a recording fake instead of actually
/// launching a child process (bevel-e7a7).
/// </summary>
public interface IExplorerSpawner
{
    /// <summary>Launch an Explorer at <paramref name="path"/>. <paramref name="selectPath"/> (a
    /// <c>reveal</c> target) is highlighted once its folder lists; <paramref name="search"/> opens
    /// straight into Find mode.</summary>
    void Spawn(string path, string? selectPath = null, bool search = false);
}

/// <summary>The real spawner: forwards to <see cref="Program.SpawnExplorer"/>.</summary>
public sealed class ProcessExplorerSpawner : IExplorerSpawner
{
    public void Spawn(string path, string? selectPath = null, bool search = false)
        => Program.SpawnExplorer(path, search: search, selectPath: selectPath);
}

/// <summary>
/// The split-mode, persistent-host implementation of the command model's window seam
/// (<see cref="IShellSurface"/>, bevel-e7a7). Bound in the TASKBAR process — the always-up host that
/// serves the bevelctl socket and bevel:// — where there is no in-process file-manager window graph to
/// address. Instead of building a <see cref="FileManager.FileManagerWindow"/> in-process (which in a
/// non-Explorer process yields a malformed, surface-less window), the window-opening verbs
/// (<c>open</c>/<c>reveal</c>) SPAWN an Explorer at the target — the split-mode equivalent of "open the
/// window". This is what makes <c>bevelctl</c>/<c>bevel://</c> work with no Explorer already open.
///
/// <para>The verbs that address a PRE-EXISTING live window — <c>select</c>-in-frontmost and the
/// window/selection queries — have no persistent-host answer: the child Explorer owns its own
/// per-process window-id space (<see cref="FileManagerWindowRegistry"/>), invisible from here, and the
/// core's OS-window projection is a disjoint id space (Dtos.cs). They return "no window" / empty rather
/// than silently succeeding, so a caller gets a clear result. Forwarding <c>select</c> to a running
/// Explorer is deferred to a follow-up taskbar→Explorer control channel.</para>
/// </summary>
public sealed class SpawningShellSurface : IShellSurface
{
    // The spawned Explorer owns its own window-id space; the taskbar host cannot see it, so window
    // verbs hand back this opaque sentinel meaning "spawned, not addressable from here".
    private static readonly WindowRef Spawned = new(0);

    private readonly IExplorerSpawner _spawner;

    public SpawningShellSurface(IExplorerSpawner spawner) => _spawner = spawner;

    public Task<WindowRef> OpenAsync(VfsPath container, ViewMode? view, CancellationToken ct)
    {
        _spawner.Spawn(container.Value);
        return Task.FromResult(Spawned);
    }

    public Task<WindowRef> RevealAsync(IReadOnlyList<VfsPath> items, bool newWindow, CancellationToken ct)
    {
        var container = items.Count > 0 ? items[0].Parent : VfsPath.Root("file");
        var select = items.Count > 0 ? items[0].Value : null;
        _spawner.Spawn(container.Value, selectPath: select);
        return Task.FromResult(Spawned);
    }

    // ── Deferred: these need a live, addressable Explorer the persistent host doesn't have ──────────

    public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct) =>
        Task.FromException(new AutomationException(
            "selecting in an existing window is not available from the taskbar host — no live file-manager window to address."));

    public Task<IReadOnlyList<WindowRef>> QueryWindowsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WindowRef>>(Array.Empty<WindowRef>());

    public Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VfsPath>>(Array.Empty<VfsPath>());

    public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct) =>
        Task.FromException(new AutomationException($"'set {prop}' is not supported from the taskbar host."));
}
