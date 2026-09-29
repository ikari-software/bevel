using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;

namespace Bevel.App;

/// <summary>
/// Spawns a new <c>--role=filer</c> process. The seam that lets the persistent-host
/// <see cref="SpawningShellSurface"/> be unit-tested with a recording fake instead of actually
/// launching a child process (bevel-e7a7).
/// </summary>
public interface IFilerSpawner
{
    /// <summary>Launch a Filer at <paramref name="path"/>. <paramref name="selectPath"/> (a
    /// <c>reveal</c> target) is highlighted once its folder lists; <paramref name="search"/> opens
    /// straight into Find mode.</summary>
    void Spawn(string path, string? selectPath = null, bool search = false);
}

/// <summary>The real spawner: forwards to <see cref="Program.SpawnFiler"/>.</summary>
public sealed class ProcessFilerSpawner : IFilerSpawner
{
    public void Spawn(string path, string? selectPath = null, bool search = false)
        => Program.SpawnFiler(path, search: search, selectPath: selectPath);
}

/// <summary>
/// The split-mode, persistent-host implementation of the command model's window seam
/// (<see cref="IShellSurface"/>, bevel-e7a7). Bound in the TASKBAR process — the always-up host that
/// serves the bevelctl socket and bevel:// — where there is no in-process file-manager window graph to
/// address. Instead of building a <see cref="FileManager.FileManagerWindow"/> in-process (which in a
/// non-Filer process yields a malformed, surface-less window), the window-opening verbs
/// (<c>open</c>/<c>reveal</c>) SPAWN a Filer at the target — the split-mode equivalent of "open the
/// window". This is what makes <c>bevelctl</c>/<c>bevel://</c> work with no Filer already open.
///
/// <para>The verbs that address a PRE-EXISTING live window — <c>select</c>-in-frontmost and the
/// window/selection queries — are FORWARDED to the running Filer processes over the
/// taskbar↔Filer control channel (<see cref="TaskbarFilerControlClient"/>, bevel-uldj): the
/// query verbs aggregate across every registered Filer (frontmost-first) and <c>select</c> routes
/// back to the Filer that owns the composite <see cref="WindowRef"/>. When that channel isn't wired
/// or no Filer is registered, they fall back to the clear empty / "no window" result — never a
/// silent success and never a crash.</para>
/// </summary>
public sealed class SpawningShellSurface : IShellSurface
{
    // The spawned Filer owns its own window-id space; the taskbar host cannot see it, so window
    // verbs hand back this opaque sentinel meaning "spawned, not addressable from here".
    private static readonly WindowRef Spawned = new(0);

    private readonly IFilerSpawner _spawner;
    // The taskbar↔Filer channel (bevel-uldj) that lets the window-coupled verbs reach the live
    // Filer processes. Null when the channel isn't wired (e.g. a non-taskbar role that still binds
    // this surface, or a test that only exercises open/reveal) — the verbs then degrade to the same
    // clear empty / "no window" result the persistent host returned before the channel existed.
    private readonly TaskbarFilerControlClient? _filers;

    public SpawningShellSurface(IFilerSpawner spawner, TaskbarFilerControlClient? filers = null)
    {
        _spawner = spawner;
        _filers = filers;
    }

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

    // ── Forwarded to the live Filers over the taskbar↔Filer channel (bevel-uldj) ─────────────
    //
    // With the channel wired, these reach the running --role=filer processes: query verbs aggregate
    // across every registered Filer (frontmost-first), and select routes back to the Filer that
    // owns the composite WindowRef. With NO Filer registered (or the channel absent) they return the
    // same clear empty / "no window" the persistent host returned before — never a crash.

    public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct) =>
        _filers is null
            ? Task.FromException(new AutomationException(
                "selecting in an existing window is not available from the taskbar host — no live file-manager window to address."))
            : _filers.SelectAsync(window, items, ct);

    public Task<IReadOnlyList<WindowRef>> QueryWindowsAsync(CancellationToken ct) =>
        _filers is null
            ? Task.FromResult<IReadOnlyList<WindowRef>>(Array.Empty<WindowRef>())
            : _filers.AggregateWindowsAsync(ct);

    public Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct) =>
        _filers is null
            ? Task.FromResult<IReadOnlyList<VfsPath>>(Array.Empty<VfsPath>())
            : _filers.QuerySelectionAsync(window, ct);

    public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct) =>
        Task.FromException(new AutomationException($"'set {prop}' is not supported from the taskbar host."));
}
