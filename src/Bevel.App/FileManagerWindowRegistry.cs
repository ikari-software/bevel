using System.Collections.Generic;
using System.Linq;
using Bevel.FileManager;

namespace Bevel.App;

/// <summary>
/// Tracks open <see cref="FileManagerWindow"/> instances so the automation surface
/// (<see cref="Bevel.Interop.IShellSurface"/> / M4-B) can address them by a stable
/// <see cref="Bevel.Interop.WindowRef"/> id and enumerate them for <c>count windows</c> /
/// <c>window 1</c>. Windows register on creation (<see cref="FileManagerWindowFactory"/>) and
/// auto-remove when <see cref="Avalonia.Controls.Window.Closed"/> fires. All access is expected on
/// the UI thread; the lock is cheap belt-and-suspenders.
/// </summary>
public sealed class FileManagerWindowRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, FileManagerWindow> _windows = new();
    private int _nextId = 1;
    // Frontmost tracking (bevel-uldj): the taskbar orders Filers frontmost-first by comparing each
    // Filer's newest activation tick, and "select in the frontmost window" resolves to the
    // most-recently-activated window WITHIN a Filer. Both are fed by the window Activated event.
    private int _frontId;
    private long _lastFocusTick;

    /// <summary>Assigns a stable id, tracks the window, and removes it again when it closes.</summary>
    public int Register(FileManagerWindow window)
    {
        int id;
        lock (_gate) { id = _nextId++; _windows[id] = window; }
        window.Closed += (_, _) => { lock (_gate) _windows.Remove(id); };
        // Record activation so the taskbar's frontmost resolution (bevel-uldj) is recency-based. A newly
        // shown window is activated by the platform, so the initial window seeds a non-zero tick too.
        window.Activated += (_, _) => { lock (_gate) { _frontId = id; _lastFocusTick = Environment.TickCount64; } };
        return id;
    }

    /// <summary>The most-recently-activated OPEN window's id (the frontmost within this process), or the
    /// earliest-registered as a fallback before any activation, or null when none are open (bevel-uldj).</summary>
    public int? FrontId()
    {
        lock (_gate)
        {
            if (_frontId != 0 && _windows.ContainsKey(_frontId)) return _frontId;
            return _windows.Count == 0 ? null : _windows.Keys.OrderBy(i => i).First();
        }
    }

    /// <summary>The newest window-activation tick seen in this process (0 before any activation) — the
    /// taskbar's frontmost-Filer ordering key (bevel-uldj).</summary>
    public long LastFocusTick { get { lock (_gate) return _lastFocusTick; } }

    public bool TryGet(int id, out FileManagerWindow window)
    {
        lock (_gate) return _windows.TryGetValue(id, out window!);
    }

    /// <summary>The id this window was registered under, or null if it isn't tracked.</summary>
    public int? IdOf(FileManagerWindow window)
    {
        lock (_gate)
            foreach (var (id, w) in _windows)
                if (ReferenceEquals(w, window)) return id;
        return null;
    }

    /// <summary>Open window ids, ascending (registration order).</summary>
    public IReadOnlyList<int> Ids()
    {
        lock (_gate) return _windows.Keys.OrderBy(i => i).ToArray();
    }

    /// <summary>The earliest-registered open window (the reuse target for <c>reveal</c>), or null.</summary>
    public FileManagerWindow? First()
    {
        lock (_gate) return _windows.OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
    }

    /// <summary>Snapshot of every open window (registration order) — for cross-window fan-out such as a
    /// live Folder Options re-list. Returns a copy so callers can iterate without holding the lock.</summary>
    public IReadOnlyList<FileManagerWindow> All()
    {
        lock (_gate) return _windows.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
    }
}
