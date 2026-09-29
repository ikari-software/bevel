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
    private readonly HelperLifecycle _helperLifecycle;
    private readonly ILogger<MacOSSystemTrayHost> _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _streamRetryInterval;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private bool _disposed;

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
            return reply.Items.Select(Map).ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("TrayService unavailable: {Message}", ex.Message);
            return Array.Empty<AbstractionsTrayItem>();
        }
    }

    /// <summary>Consolidation state notification (bevel-qpir). The NATIVE hide — the macOS control
    /// <c>NSStatusItem</c> — is owned by the TASKBAR process (<see cref="MacMenuBarControl"/> via the
    /// shell-core local-hide seam), because it needs a serviced AppKit run loop and this host runs in the
    /// headless core. This call only forwards the state to the helper's repurposed SetConsolidation RPC so
    /// its tray-poll cadence adapts (900ms while consolidated — the tray IS the menu bar then — vs 2s idle).
    /// Best-effort and bounded: a wedged/disconnected helper must never hang the settings-apply path.</summary>
    public async Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default)
    {
        if (_disposed) return;
        try
        {
            var tray = GetTrayClient();
            await tray.SetConsolidationAsync(new SetConsolidationRequest { Enabled = hidden },
                headers: AuthHeader(), cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
        {
            _logger.LogWarning("TrayService unavailable for consolidation notify: {Message}", ex.Message);
        }
        catch (Exception ex) { _logger.LogWarning("consolidation notify failed: {Message}", ex.Message); }
    }

    public async Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
        bool park = false, CancellationToken ct = default)
    {
        if (_disposed) return false;
        // Bound the click on a wedged helper (bevel-dem): forwardClick can walk several AX calls, each
        // capped ~1s helper-side, so a 3s deadline keeps a dead tray click from hanging the caller. The
        // park path (bevel-6fin) additionally runs a self-addressed event move + reflow wait before the
        // press, so give it a longer deadline.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(park ? 8 : 3));
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
                Park = park,
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
                            ItemAdded?.Invoke(this, item);
                            break;
                        case TrayChange.Types.Kind.Updated:
                            ItemUpdated?.Invoke(this, item);
                            break;
                        case TrayChange.Types.Kind.Removed:
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
