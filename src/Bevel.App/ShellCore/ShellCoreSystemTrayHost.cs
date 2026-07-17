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

    public Capabilities Capabilities { get; } =
        new(Available: true, TrayMode: TrayCapability.Mirrored,
            Notes: new[] { "macos-tray: mirrored via shell-core" }, SupportsReposition: false);

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
