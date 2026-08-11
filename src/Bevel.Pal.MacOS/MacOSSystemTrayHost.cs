using Bevel.Ipc;
using Bevel.Ipc.V1;
using Bevel.Pal.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using AbstractionsTrayItem = Bevel.Pal.Abstractions.TrayItem;
using WireTrayItem = Bevel.Ipc.V1.TrayItem;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS <see cref="ISystemTrayHost"/> over the helper's TrayService gRPC client (bevel-m3.1).
/// Mirrors the menu-bar status items the helper discovers (spec §5) — a <see cref="TrayCapability.Mirrored"/>
/// tray — mapping the wire <see cref="WireTrayItem"/> to the abstraction DTO and re-raising the helper's
/// snapshot/deltas as <see cref="ItemAdded"/>/<see cref="ItemRemoved"/>/<see cref="ItemUpdated"/>.
/// The helper owns discovery; this layer does not re-poll. Same client + resubscribe pattern as
/// <see cref="MacOSWindowManager"/>.
/// </summary>
public sealed class MacOSSystemTrayHost : ISystemTrayHost, IDisposable
{
    // Strategy C hide mechanism (bevel-7hf4): true = OVERLAY-HIDE (cover the on-screen items with a
    // level-26 NSPanel so their captures stay live); false = the legacy off-screen push
    // (MacMenuBarControl, which freezes captures). Kept as a one-line revert if a specific app freezes
    // under full occlusion.
    private const bool UseOverlayHide = false;   // overlay v1 mis-covered the whole bar + killed translucency; reverted pending fix

    private readonly HelperLifecycle _helperLifecycle;
    private readonly ILogger<MacOSSystemTrayHost> _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _streamRetryInterval;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private bool _disposed;

    // Live cache of the mirrored items' screen bounds (keyed by wire item id), fed by the Changes stream
    // and GetItemsAsync. The overlay's cover frame is the union of these; caching here keeps the platform
    // detail out of the abstraction (ISystemTrayHost.SetNativeTrayHiddenAsync stays bounds-free).
    private readonly object _boundsLock = new();
    private readonly Dictionary<string, PalRect> _lastBounds = new();

    private static readonly Capabilities TrayCapabilities = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "macos-tray: mirrored (Ice technique)" },
        SupportsReposition: false);

    public Capabilities Capabilities => TrayCapabilities;

    public event EventHandler<AbstractionsTrayItem>? ItemAdded;
    public event EventHandler<AbstractionsTrayItem>? ItemRemoved;
    public event EventHandler<AbstractionsTrayItem>? ItemUpdated;

    public MacOSSystemTrayHost(HelperLifecycle helperLifecycle, ILogger<MacOSSystemTrayHost> logger)
        : this(helperLifecycle, logger, SystemClock.Instance, TimeSpan.FromSeconds(2))
    {
    }

    internal MacOSSystemTrayHost(HelperLifecycle helperLifecycle, ILogger<MacOSSystemTrayHost> logger,
        IClock clock, TimeSpan streamRetryInterval)
    {
        _helperLifecycle = helperLifecycle;
        _logger = logger;
        _clock = clock;
        _streamRetryInterval = streamRetryInterval;
    }

    // ── ISystemTrayHost ─────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<AbstractionsTrayItem>> GetItemsAsync(CancellationToken ct = default)
    {
        if (_disposed) return Array.Empty<AbstractionsTrayItem>();
        try
        {
            var tray = GetTrayClient();
            var reply = await tray.ListTrayItemsAsync(new ListTrayItemsRequest(),
                headers: AuthHeader(), cancellationToken: ct);
            var items = reply.Items.Select(Map).ToList();
            foreach (var it in items) CacheBounds(it);
            return items;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("TrayService unavailable: {Message}", ex.Message);
            return Array.Empty<AbstractionsTrayItem>();
        }
    }

    /// <summary>Menu-bar consolidation (Strategy C, bevel-7hf4): hide the real status items into Bevel's
    /// tray (control-item expansion) or reveal them. Drives the helper's SetConsolidation RPC. Bounded by
    /// a 3s deadline so a wedged helper can't hang the settings-apply path.</summary>
    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        // Strategy C (bevel-7hf4): the hide lives in THIS app process, which has a real NSApplication run
        // loop — NOT the gRPC helper, whose hand-rolled run loop deadlocks the status-bar IPC. Driven via
        // ObjC interop on the AppKit main thread; the settings-apply callers are on the UI thread. No
        // helper round-trip for the hide.
        try
        {
            if (UseOverlayHide)
                MacMenuBarOverlay.SetHidden(hidden, ComputeMirroredStrip());   // cover on-screen items (live captures)
            else
                MacMenuBarControl.SetHidden(hidden);                            // legacy off-screen push (freezes captures)
        }
        catch (Exception ex) { _logger.LogWarning("consolidation apply failed: {Message}", ex.Message); }
        return Task.CompletedTask;
    }

    public async Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
        CancellationToken ct = default)
    {
        if (_disposed) return false;
        // Bound the click on a wedged helper (bevel-dem): forwardClick can walk several AX calls, each
        // capped ~1s helper-side, so a 3s deadline keeps a dead tray click from hanging the caller.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var tray = GetTrayClient();
            var reply = await tray.ForwardClickAsync(new ForwardClickRequest
            {
                ItemId = id.Value,
                Button = button == TrayButton.Right
                    ? ForwardClickRequest.Types.Button.Right
                    : ForwardClickRequest.Types.Button.Left,
                Modifiers = (uint)modifiers,
            }, headers: AuthHeader(), cancellationToken: cts.Token);
            return reply.Delivered;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
        {
            _logger.LogWarning("TrayService unavailable for ForwardClick: {Message}", ex.Message);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;   // our deadline fired (not the caller cancelling) — treat as not delivered
        }
    }

    // ── Changes stream (helper is the discovery authority) ──────────────

    /// <summary>Subscribes to the TrayService Changes stream: applies the SNAPSHOT burst, then live
    /// add/remove/update deltas. Idempotent; call once at startup (parallels the window manager).</summary>
    public Task StartPollAsync(CancellationToken ct = default)
    {
        if (_streamTask is not null) return Task.CompletedTask;
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamTask = StreamLoopAsync(_streamCts.Token);
        return Task.CompletedTask;
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tray = GetTrayClient();
                using var call = tray.Changes(new TrayChangesRequest(), headers: AuthHeader(), cancellationToken: ct);

                await foreach (var change in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (change.Item is null) continue;
                    var item = Map(change.Item);
                    switch (change.Kind)
                    {
                        case TrayChange.Types.Kind.Snapshot:
                        case TrayChange.Types.Kind.Added:
                            CacheBounds(item);
                            ItemAdded?.Invoke(this, item);
                            break;
                        case TrayChange.Types.Kind.Updated:
                            CacheBounds(item);
                            ItemUpdated?.Invoke(this, item);
                            break;
                        case TrayChange.Types.Kind.Removed:
                            lock (_boundsLock) _lastBounds.Remove(item.Id.Value);
                            ItemRemoved?.Invoke(this, item);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                _logger.LogWarning("Tray Changes stream unavailable, retrying: {Message}", ex.Message);
                await _clock.Delay(_streamRetryInterval, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tray Changes stream failed");
                await _clock.Delay(_streamRetryInterval, ct);
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private TrayService.TrayServiceClient GetTrayClient()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSSystemTrayHost));
        var channel = _helperLifecycle.Client.GetChannel();
        if (channel is null) throw new InvalidOperationException("Helper not connected.");
        return new TrayService.TrayServiceClient(channel);
    }

    private Grpc.Core.Metadata AuthHeader()
        => HelperClient.BuildAuthMetadata(_helperLifecycle.NonceToken, "tray");

    // ── Overlay cover-frame geometry (bevel-7hf4) ───────────────────────

    /// <summary>Records a mirrored item's screen bounds for the overlay cover frame. Called on the gRPC
    /// stream thread and from GetItemsAsync; lock-protected because <see cref="ComputeMirroredStrip"/> reads
    /// it on the UI thread.</summary>
    private void CacheBounds(AbstractionsTrayItem item)
    {
        if (item.Bounds is not { } b) return;
        lock (_boundsLock) _lastBounds[item.Id.Value] = b;
    }

    /// <summary>The union of the mirrored items' bounds (global top-left CG points) that fall in the
    /// primary-display menu-bar band — the exact strip the overlay must cover so the clock / Control
    /// Center (never mirrored, right of this union) stay visible. Null when there is nothing to cover.
    /// Cheap and pure; safe to call on the UI thread.</summary>
    private PalRect? ComputeMirroredStrip()
    {
        lock (_boundsLock)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var any = false;
            foreach (var b in _lastBounds.Values)
            {
                if (b.Width <= 0 || b.Height <= 0) continue;
                if (b.X < 0) continue;                 // v1: primary display only (secondary items have offset x)
                if (b.Y > 40) continue;                // menu-bar band only (items sit at CG y ≈ 0)
                any = true;
                if (b.X < minX) minX = b.X;
                if (b.Y < minY) minY = b.Y;
                if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
            }
            if (!any) return null;
            return new PalRect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    private static AbstractionsTrayItem Map(WireTrayItem w)
        => new(
            new TrayItemId(w.ItemId),
            w.Tooltip,
            OwnerBundleId: string.IsNullOrEmpty(w.OwnerBundleId) ? null : w.OwnerBundleId,
            OwnerName: string.IsNullOrEmpty(w.OwnerName) ? null : w.OwnerName,
            IconPng: w.IconPng.IsEmpty ? null : w.IconPng.ToByteArray(),
            Bounds: w.Bounds is null ? null : new PalRect(w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height),
            IsLive: w.IsLive);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
    }
}
