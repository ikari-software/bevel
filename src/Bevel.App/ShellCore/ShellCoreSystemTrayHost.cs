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

    public ShellCoreSystemTrayHost(ShellCoreClient core)
    {
        _core = core;
        _core.EventReceived += OnCoreEvent;
    }

    /// <summary>
    /// Capabilities of the tray this proxy fronts. This used to be hardcoded to
    /// <see cref="TrayCapability.Mirrored"/> with a "macos-tray" note — but the proxy backs the taskbar
    /// role on EVERY platform, so on Windows it claimed to mirror a menu bar that does not exist
    /// (bevel-traycaps). Anything gating on TrayMode therefore got the macOS answer everywhere.
    ///
    /// <para>Reported per-platform to match what the core's real PAL says: macOS can only ever mirror
    /// another process's menu bar, while Windows shells own the tray protocol outright. This duplicates
    /// the truth that lives in the core's PAL, which is why it is still wrong in principle — the core
    /// should FORWARD its capabilities over the IPC snapshot and this proxy should report those verbatim.
    /// Tracked separately; this at least stops the proxy asserting a platform it isn't on.</para>
    /// </summary>
    public Capabilities Capabilities { get; } = OperatingSystem.IsMacOS()
        ? new Capabilities(Available: true, TrayMode: TrayCapability.Mirrored,
            Notes: new[] { "macos-tray: mirrored via shell-core" }, SupportsReposition: false)
        : new Capabilities(Available: true, TrayMode: TrayCapability.Authoritative,
            Notes: new[] { "tray: authoritative, via shell-core" }, SupportsReposition: false);

    private void OnCoreEvent(CoreEvent e)
    {
        switch (e.Kind)
        {
            case CoreEventKind.TraySnapshot when e.TrayItems is not null:
                // Replay the snapshot as Added events — TrayViewModel upserts idempotently by id.
                foreach (var t in e.TrayItems)
                    ItemAdded?.Invoke(this, t);
                break;
            case CoreEventKind.TrayItemAdded when e.TrayItem is { } ta:
                ItemAdded?.Invoke(this, ta);
                break;
            case CoreEventKind.TrayItemUpdated when e.TrayItem is { } tu:
                ItemUpdated?.Invoke(this, tu);
                break;
            case CoreEventKind.TrayItemRemoved when e.TrayItem is { } tr:
                ItemRemoved?.Invoke(this, tr);
                break;
            // Window/app events are handled by the other shell-core adapters on the same connection.
        }
    }

    /// <summary>The tray arrives as the TraySnapshot event burst on connect (like windows), so the
    /// pull is empty — the stream is the source of truth.</summary>
    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    /// <summary>Menu-bar reclaim (spec §5.4) is owned by the core when it lands (M3-F); no-op for now.</summary>
    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
        CancellationToken ct = default)
    {
        if (!_core.IsConnected) return false;
        try
        {
            var r = await _core.SendAsync(new CoreCommand(CoreCommandKind.ForwardTrayClick,
                TrayItemId: id.Value, TrayButton: button, TrayModifiers: modifiers), ct).ConfigureAwait(false);
            return r.Ok && r.Delivered == true;
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) when (!_core.IsConnected) { return false; }
    }

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}
