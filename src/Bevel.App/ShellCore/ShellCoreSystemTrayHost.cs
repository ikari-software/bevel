using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's <see cref="ISystemTrayHost"/> that is really a thin client of the shell-core owner
/// (bevel-m3.1.1): the core owns the single tray projection (one TrayService stream to the Swift
/// helper) and pushes deltas here, which this adapter re-raises as the ordinary tray events the
/// taskbar's <c>TrayViewModel</c> consumes. Click-forwarding becomes a core request. So the split
/// taskbar never touches the helper — tray discovery/capture all live in the core, exactly like
/// windows (<see cref="ShellCoreWindowManager"/>).
/// </summary>
public sealed class ShellCoreSystemTrayHost : ISystemTrayHost
{
    private readonly ShellCoreClient _core;

    /// <summary>The NATIVE hide, applied in THIS process (bevel-qpir). The shell-core owner (the core) is
    /// headless — no serviced AppKit run loop — so it must never own the macOS control NSStatusItem (that
    /// is what wedged the helper: status-bar IPC needs a real run loop). The taskbar process has one (Avalonia),
    /// so the composition root wires this to <c>MacMenuBarControl.SetHidden</c> on macOS (and the Windows
    /// <c>TrayNotifyWnd</c> toggle on Windows). Null on PALs with no native hide.</summary>
    private readonly Action<bool>? _applyLocalHide;

    /// <summary>What this adapter has raised and not yet removed, by id (bevel-ri8c). A TraySnapshot is a
    /// full reconcile: whatever is in here but absent from the snapshot vanished while the link was down and
    /// must be removed. Also makes the <see cref="GetItemsAsync"/> pull order-safe (see
    /// <see cref="ProjectionMirror{T}"/>); events arrive on the transport thread, so it is thread-safe.</summary>
    private readonly ProjectionMirror<TrayItem> _known = new(t => t.Id.Value);

    /// <summary>Test seam: awaited after the pull's reply arrives and before it is merged — lets a test land a
    /// remove delta deterministically in that window.</summary>
    internal Func<Task>? BeforePullMergeForTest;

    public ShellCoreSystemTrayHost(ShellCoreClient core, Action<bool>? applyLocalHide = null)
    {
        _core = core;
        _applyLocalHide = applyLocalHide;
        _core.EventReceived += OnCoreEvent;
    }

    /// <summary>bevel-mhvu: the core FORWARDs its tray host's capabilities on the TraySnapshot; this
    /// proxy reports them verbatim. The per-platform literal remains only as the pre-snapshot fallback
    /// (older cores send no capability slot — the nullable field keeps them wire-compatible).</summary>
    private volatile Capabilities? _forwarded;
    private static Capabilities Fallback => OperatingSystem.IsMacOS()
        ? new Capabilities(Available: true, TrayMode: TrayCapability.Mirrored,
            Notes: new[] { "macos-tray: mirrored via shell-core" }, SupportsReposition: false)
        : new Capabilities(Available: true, TrayMode: TrayCapability.Authoritative,
            Notes: new[] { "tray: authoritative, via shell-core" }, SupportsReposition: false);

    public Capabilities Capabilities => _forwarded ?? Fallback;

    // internal = test seam: tests feed synthetic CoreEvents without a live server.
    internal void OnCoreEvent(CoreEvent e)
    {
        switch (e.Kind)
        {
            case CoreEventKind.TraySnapshot:
                if (e.TrayCapabilities is { } caps) _forwarded = caps;   // bevel-mhvu: forward verbatim
                if (e.TrayItems is null) break;
            {
                // (fall through to the snapshot merge below)

                // A snapshot is authoritative (bevel-ri8c): remove what it omits, then (re)add what it
                // lists. Adds alone left ghosts for icons that vanished while the link was down.
                var gone = _known.ApplySnapshot(e.TrayItems);
                foreach (var g in gone)
                    ItemRemoved?.Invoke(this, g);
                foreach (var t in e.TrayItems)
                    ItemAdded?.Invoke(this, t);
                break;
            }
            case CoreEventKind.TrayItemAdded when e.TrayItem is { } ta:
                _known.Upsert(ta);
                ItemAdded?.Invoke(this, ta);
                break;
            case CoreEventKind.TrayItemUpdated when e.TrayItem is { } tu:
                _known.Upsert(tu);
                ItemUpdated?.Invoke(this, tu);
                break;
            case CoreEventKind.TrayItemRemoved when e.TrayItem is { } tr:
                _known.Remove(tr);
                ItemRemoved?.Invoke(this, tr);
                break;
            // Window/app events are handled by the other shell-core adapters on the same connection.
        }
    }

    /// <summary>The tray's pull backstop (bevel-4zfs): GetTrayItems asks the core's live projection for
    /// the current set — every other projection always had a pull (windows/apps/settings); the tray was
    /// stream-only, which is exactly why its races were unhealable before the session protocol. The
    /// stream (Hello snapshot + deltas) remains the primary feed; this is the reconcile path.</summary>
    public async ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
    {
        var start = _known.BeginPull();
        try
        {
            var r = await _core.SendAsync(new CoreCommand(CoreCommandKind.GetTrayItems), ct).ConfigureAwait(false);
            var items = r.TrayItems ?? Array.Empty<TrayItem>();
            if (BeforePullMergeForTest is { } gate) await gate().ConfigureAwait(false);
            // Record pulled items too (bevel-ri8c) so a later snapshot can reap them — except any removed
            // since this pull began: the pull is older than that removal. They are dropped from the result
            // as well, so the caller (TrayViewModel upserts it) cannot resurrect them either.
            return _known.MergePull(start, items);
        }
        catch
        {
            return Array.Empty<TrayItem>(); // a pull during a dropped link is just empty — the stream re-syncs
        }
        finally
        {
            _known.EndPull();
        }
    }

    /// <summary>Menu-bar consolidation (bevel-qpir): (1) apply the NATIVE hide locally — in THIS taskbar
    /// process, which has the real AppKit run loop the macOS control item needs — then (2) tell the core so
    /// it can forward the state to the helper for its poll cadence (900ms while consolidated). Step (2) is
    /// best-effort: a disconnected core just means the helper keeps its idle cadence until the next change.</summary>
    public async Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default)
    {
        try { _applyLocalHide?.Invoke(hidden); }
        catch (Exception) { /* best-effort: never hang the settings-apply path on a native failure */ }
        if (!_core.IsConnected) return;
        try
        {
            await _core.SendAsync(new CoreCommand(CoreCommandKind.SetTrayHidden, Hidden: hidden), ct)
                .ConfigureAwait(false);
        }
        catch (Exception) { /* best-effort cadence notify — the hide itself already applied */ }
    }

    public async Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
        bool park = false, CancellationToken ct = default)
    {
        if (!_core.IsConnected) return false;
        try
        {
            var r = await _core.SendAsync(new CoreCommand(CoreCommandKind.ForwardTrayClick,
                TrayItemId: id.Value, TrayButton: button, TrayModifiers: modifiers, Park: park), ct)
                .ConfigureAwait(false);
            return r.Ok && r.Delivered == true;
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) when (!_core.IsConnected) { return false; }
    }

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}
