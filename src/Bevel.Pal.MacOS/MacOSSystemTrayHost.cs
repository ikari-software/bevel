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

    /// <summary>What this adapter has raised and not yet removed, by id (bevel-voqo / bevel-35r9).
    /// Touched only by the stream loop (and by tests through the same two methods), so it needs no lock.</summary>
    private readonly Dictionary<string, AbstractionsTrayItem> _raised = new(StringComparer.Ordinal);

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
        => await TrySeedItemsAsync(ct).ConfigureAwait(false) ?? Array.Empty<AbstractionsTrayItem>();

    /// <summary>Null = the helper could not be reached (disposed / TrayService Unavailable); a list (maybe
    /// empty) = the helper answered. <see cref="GetItemsAsync"/> folds the null into empty for UI callers;
    /// the core's seed uses this so a down helper is not published as an authoritative empty tray (bevel-ri8c).</summary>
    public async ValueTask<IReadOnlyList<AbstractionsTrayItem>?> TrySeedItemsAsync(CancellationToken ct = default)
    {
        if (_disposed) return null;
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
            return null;
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

                // Subscribe FIRST, then list: a removal that happens after the list is already on
                // its way down the stream. The other order loses it.
                await ReconcileAfterResubscribeAsync(TrySeedItemsAsync, ct).ConfigureAwait(false);

                await foreach (var change in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (change.Item is null) continue;
                    Apply(change.Kind, Map(change.Item));
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

    /// <summary>How long the reconcile's one list may take before the stream is read without it.</summary>
    internal static readonly TimeSpan ReconcileListDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Removes what vanished while the stream was down (bevel-voqo, the tray half of bevel-35r9). The
    /// stream's snapshot burst is upsert-only and has no end marker, so it can never say "this item is
    /// gone"; without this an item that quit during a helper restart stays in the core's projection,
    /// and on the taskbar, for good. One list per (re)subscribe, never periodic.
    /// <para>A null list means the helper could not be asked. That is not an empty tray: nothing is
    /// removed, and the next resubscribe tries again. A list that fails or does not answer within
    /// <see cref="ReconcileListDeadline"/> is the same: this is a cleanup, and it must never cost
    /// the subscription it runs in front of.</para>
    /// </summary>
    internal async ValueTask ReconcileAfterResubscribeAsync(
        Func<CancellationToken, ValueTask<IReadOnlyList<AbstractionsTrayItem>?>> list, CancellationToken ct)
    {
        if (_raised.Count == 0) return; // first subscribe: nothing raised, nothing to take back
        IReadOnlyList<AbstractionsTrayItem>? listed;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ReconcileListDeadline);
        try
        {
            listed = await list(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Tray reconcile list failed, nothing removed: {Message}", ex.Message);
            return;
        }
        if (listed is null) return;
        foreach (var gone in SnapshotReconcile.Absent(_raised, listed, i => i.Id.Value))
            Apply(TrayChange.Types.Kind.Removed, gone);
    }

    /// <summary>One stream change: raise its event and keep <see cref="_raised"/> in step.</summary>
    internal void Apply(TrayChange.Types.Kind kind, AbstractionsTrayItem item)
    {
        switch (kind)
        {
            case TrayChange.Types.Kind.Snapshot:
            case TrayChange.Types.Kind.Added:
                _raised[item.Id.Value] = item;
                ItemAdded?.Invoke(this, item);
                break;
            case TrayChange.Types.Kind.Updated:
                _raised[item.Id.Value] = item;
                ItemUpdated?.Invoke(this, item);
                break;
            case TrayChange.Types.Kind.Removed:
                _raised.Remove(item.Id.Value);
                ItemRemoved?.Invoke(this, item);
                break;
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
        => _helperLifecycle.AuthFor("tray");

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
