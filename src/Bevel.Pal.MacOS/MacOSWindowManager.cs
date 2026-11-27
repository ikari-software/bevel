using System.Runtime.CompilerServices;
using Bevel.Ipc;
using Bevel.Ipc.V1;
using Bevel.Pal.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS IWindowManager over the WindowService gRPC client.
/// Maps wire TaskbarWindow/PixelRect to Bevel.Pal.Abstractions.ForeignWindow/PalRect,
/// subscribes to the Changes stream for live events. The helper's reconciliation
/// poll (500ms) is the single diff authority — this layer does NOT re-diff via
/// ListWindows; a second C# reconcile was re-emitting spurious open/close events.
/// </summary>
public sealed class MacOSWindowManager : IWindowManager, IDisposable
{
    private readonly HelperLifecycle _helperLifecycle;
    private readonly ILogger<MacOSWindowManager> _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _streamRetryInterval;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private readonly Dictionary<string, ForeignWindow> _knownWindows = new();
    private bool _disposed;

    private static readonly Capabilities MacOSCapabilities = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "macos-window-manager" },
        SupportsReposition: true);

    public Capabilities Capabilities => MacOSCapabilities;

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;

    public MacOSWindowManager(HelperLifecycle helperLifecycle, ILogger<MacOSWindowManager> logger)
        : this(helperLifecycle, logger, SystemClock.Instance, TimeSpan.FromSeconds(2))
    {
    }

    internal MacOSWindowManager(HelperLifecycle helperLifecycle, ILogger<MacOSWindowManager> logger,
        IClock clock, TimeSpan streamRetryInterval,
        Func<CancellationToken, IAsyncEnumerable<WindowChange>>? changesSource = null)
    {
        _helperLifecycle = helperLifecycle;
        _logger = logger;
        _clock = clock;
        _streamRetryInterval = streamRetryInterval;
        _changesSource = changesSource;
    }

    // bevel-umgr test seam: null means the real gRPC stream (GrpcChanges); tests inject a fake
    // IAsyncEnumerable so retry/cancel/change-kind behavior is driven without a helper.
    private readonly Func<CancellationToken, IAsyncEnumerable<WindowChange>>? _changesSource;

    // ── IWindowManager ──────────────────────────────────────────────────

    // A wedged helper (blocked on an AX call to a hung app) must not hang the awaiting UI action
    // indefinitely (bevel-dem). Every interactive command carries a bounded deadline: on expiry the
    // linked token cancels the RPC and the caller's catch treats it as "helper unavailable → next
    // poll reconciles", instead of a dead click. The helper self-caps AX messaging at ~1s per element,
    // and forwardClick can walk several, so 3s leaves headroom while staying imperceptible on failure.
    private static readonly TimeSpan InteractiveRpcTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A CTS linked to <paramref name="ct"/> that also cancels after the interactive deadline.
    /// Dispose it (via <c>using</c>) after the call.</summary>
    private static CancellationTokenSource TimeoutScope(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(InteractiveRpcTimeout);
        return cts;
    }

    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => await EnumerateAsync(skipIcons: false, ct).ConfigureAwait(false);

    /// <summary>Enumerate windows with an option to skip app icons (for lightweight polling).</summary>
    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(bool skipIcons, CancellationToken ct = default)
        => await TrySeedWindowsAsync(skipIcons, ct).ConfigureAwait(false) ?? Array.Empty<ForeignWindow>();

    /// <summary>Null = the helper could not be reached (disposed / WindowService Unavailable); a list (maybe
    /// empty) = the helper answered. <see cref="EnumerateAsync"/> folds the null into empty for its callers;
    /// the core's seed uses this so a down helper is not published as an authoritative empty window list,
    /// which would close every button on a surviving taskbar (bevel-wheq). Other failures (e.g. "Helper not
    /// connected" before the channel exists) still throw, as before — the seed retries those.</summary>
    public async ValueTask<IReadOnlyList<ForeignWindow>?> TrySeedWindowsAsync(CancellationToken ct = default)
        => await TrySeedWindowsAsync(skipIcons: false, ct).ConfigureAwait(false);

    /// <summary>Enumerate windows with an option to skip app icons.</summary>
    public async ValueTask<IReadOnlyList<ForeignWindow>?> TrySeedWindowsAsync(bool skipIcons, CancellationToken ct = default)
    {
        if (_disposed) return null;

        try
        {
            var ws = GetWindowClient();
            var reply = await ws.ListWindowsAsync(new ListWindowsRequest { SkipIcons = skipIcons },
                headers: AuthHeader(), cancellationToken: ct);
            return reply.Windows.Select(Map).ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("WindowService unavailable: {Message}", ex.Message);
            return null;
        }
    }

    public async Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.ActivateAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.MinimizeAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.RestoreAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.RestoreAndActivateAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task CloseAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.CloseAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task ZoomAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.ZoomAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        if (string.IsNullOrEmpty(bundleId)) return;
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.TerminateAppAsync(new TerminateAppRequest { BundleId = bundleId, Force = force },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task HideAppAsync(string bundleId, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        if (string.IsNullOrEmpty(bundleId)) return;
        var ws = GetWindowClient();
        using var cts = TimeoutScope(ct);
        await ws.HideAppAsync(new HideAppRequest { BundleId = bundleId },
            headers: AuthHeader(), cancellationToken: cts.Token);
    }

    public async Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        if (_disposed) return null;
        // Intrinsic deadline (bevel-1275): capture was the one window RPC that forwarded the caller's
        // token verbatim with no bound of its own, so a cold/wedged ScreenCaptureKit init in the helper
        // — the slow first SCShareableContent after a Screen-Recording grant — could hang the call for as
        // long as the caller allowed, and the split-mode/core caller supplies no timeout at all. Bound it
        // like every sibling RPC via TimeoutScope. On the intrinsic deadline we return null so the caller
        // falls back to the static app icon (limited mode); only a caller-driven cancellation propagates.
        using var cts = TimeoutScope(ct);
        try
        {
            var ws = GetWindowClient();
            var reply = await ws.CaptureWindowAsync(
                new CaptureWindowRequest
                {
                    WindowId = id.Value,
                    MaxWidth = (uint)Math.Max(0, maxWidth),
                    MaxHeight = (uint)Math.Max(0, maxHeight),
                },
                headers: AuthHeader(), cancellationToken: cts.Token);
            return reply.Png.IsEmpty ? null : reply.Png.ToByteArray();
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();  // caller cancelled (e.g. hover moved on) → honor it
            return null;                        // intrinsic timeout / helper down / capture unavailable → no preview
        }
    }

    public async Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null,
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        try
        {
            var ws = GetWindowClient();
            var request = new RepositionRequest
            {
                WindowId = id.Value,
                Target = new PixelRect { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height },
            };
            if (expected is { } e)   // compare-and-set: the helper checks the live AX frame (bevel-tm1n)
                request.Expected = new PixelRect { X = e.X, Y = e.Y, Width = e.Width, Height = e.Height };
            await ws.RepositionAsync(request, headers: AuthHeader(), cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            // The window moved since the caller's snapshot: a no-op, like a window that has gone.
            _logger.LogDebug("Reposition skipped for {Id}: {Message}", id, ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("Reposition unavailable for {Id}: {Message}", id, ex.Message);
        }
    }

    // ── Changes stream (helper is the diff authority) ─────────────────

    public Task StartPollAsync(CancellationToken ct = default)
    {
        if (_streamTask is not null) return Task.CompletedTask;

        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamTask = StreamLoopAsync(_streamCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to the Changes stream: applies the initial SNAPSHOT, then live
    /// deltas (opened/closed/focused/title/moved/minimized) as the helper emits them.
    /// </summary>
    private async Task StreamLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var source = _changesSource ?? GrpcChanges;
                await foreach (var change in source(ct))
                    Apply(change.Kind, change.Window is null ? null : Map(change.Window));
            }
            catch (OperationCanceledException) { break; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                // Helper not up yet — wait and resubscribe.
                _logger.LogWarning("Changes stream unavailable, retrying: {Message}", ex.Message);
                await _clock.Delay(_streamRetryInterval, ct);
            }
            catch (Exception ex)
            {
                // Includes "Helper not connected" (InvalidOperationException) during the
                // startup window before the socket is ready. Delay before resubscribing so
                // we don't tight-loop and flood the log until the helper connects.
                _logger.LogWarning(ex, "Changes stream failed");
                await _clock.Delay(_streamRetryInterval, ct);
            }
        }
    }

    /// <summary>The production changes source: subscribe to the gRPC stream, then — subscribe FIRST,
    /// then list, so a close that happens after the list is already on its way down the stream is not
    /// lost (same discipline as the tray host) — reconcile, then yield the live deltas.</summary>
    private async IAsyncEnumerable<WindowChange> GrpcChanges([EnumeratorCancellation] CancellationToken ct)
    {
        var ws = GetWindowClient();
        using var call = ws.Changes(new ChangesRequest(), headers: AuthHeader(), cancellationToken: ct);
        await ReconcileAfterResubscribeAsync(c => TrySeedWindowsAsync(skipIcons: true, c), ct).ConfigureAwait(false);
        await foreach (var change in call.ResponseStream.ReadAllAsync(ct))
            yield return change;
    }

    /// <summary>
    /// Removes what vanished while the stream was down (bevel-35r9, the window half; the tray half
    /// is bevel-voqo in <see cref="MacOSSystemTrayHost"/>). The stream's snapshot burst is
    /// upsert-only and has no end marker, so it can never say "this window is gone"; without this
    /// a window that closed during a helper restart stays in the core's projection — and on the
    /// taskbar — for good. One list per (re)subscribe, never periodic.
    /// <para>A null list means the helper could not be asked. That is not an empty window list:
    /// nothing is closed, and the next resubscribe tries again. A list that fails or does not
    /// answer within the deadline is the same: this is a cleanup, and it must never cost the
    /// subscription it runs in front of.</para>
    /// </summary>
    internal async ValueTask ReconcileAfterResubscribeAsync(
        Func<CancellationToken, ValueTask<IReadOnlyList<ForeignWindow>?>> list, CancellationToken ct)
    {
        if (_knownWindows.Count == 0) return; // first subscribe: nothing raised, nothing to take back
        IReadOnlyList<ForeignWindow>? listed;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(MacOSSystemTrayHost.ReconcileListDeadline);
        try
        {
            listed = await list(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Window reconcile list failed, nothing closed: {Message}", ex.Message);
            return;
        }
        if (listed is null) return;
        foreach (var gone in SnapshotReconcile.Absent(_knownWindows, listed, w => w.Id.Value))
            Apply(WindowChange.Types.Kind.Closed, gone);
    }

    /// <summary>One stream change: raise its event and keep <see cref="_knownWindows"/> in step with
    /// the core's projection (upsert on every kind that carries a window, forget on Closed).</summary>
    internal void Apply(WindowChange.Types.Kind kind, ForeignWindow? w)
    {
        switch (kind)
        {
            case WindowChange.Types.Kind.Snapshot:
                if (w is not null)
                {
                    _knownWindows[w.Id.Value] = w;
                    WindowChanged?.Invoke(this, w);
                }
                break;
            case WindowChange.Types.Kind.Opened:
                if (w is not null)
                {
                    _knownWindows[w.Id.Value] = w;
                    WindowOpened?.Invoke(this, w);
                }
                break;
            case WindowChange.Types.Kind.Closed:
                if (w is not null)
                {
                    _knownWindows.Remove(w.Id.Value);
                    WindowClosed?.Invoke(this, w);
                }
                break;
            case WindowChange.Types.Kind.Focused:
                if (w is not null)
                {
                    _knownWindows[w.Id.Value] = w;
                    WindowChanged?.Invoke(this, w);
                    if (w.IsFocused)
                        ForegroundChanged?.Invoke(this, w);
                }
                break;
            case WindowChange.Types.Kind.TitleChanged:
            case WindowChange.Types.Kind.Moved:
            case WindowChange.Types.Kind.Minimized:
            case WindowChange.Types.Kind.Deminimized:
                if (w is not null)
                {
                    _knownWindows[w.Id.Value] = w;
                    WindowChanged?.Invoke(this, w);
                }
                break;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private WindowService.WindowServiceClient GetWindowClient()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var channel = _helperLifecycle.Client.GetChannel();
        if (channel is null) throw new InvalidOperationException("Helper not connected.");
        return new WindowService.WindowServiceClient(channel);
    }

    private Grpc.Core.Metadata AuthHeader()
        => _helperLifecycle.AuthFor("window");

    // internal (not private) so Bevel.Pal.MacOS.Tests can pin the uint64→long boundary clamp.
    internal static ForeignWindow Map(TaskbarWindow tw)
    {
        // AppId is only ever used as the taskbar button's label fallback (when the
        // window has no title), so prefer the friendly app name ("Google Chrome")
        // over the bundle id ("com.google.Chrome"). proto3 strings default to ""
        // (never null), so fall back explicitly on empty.
        var appId = string.IsNullOrEmpty(tw.AppName) ? tw.AppBundleId : tw.AppName;
        var icon = tw.AppIconPng.ToByteArray();
        // App-presence entries (bevel-ww71) carry no frame — guard the message-type field, which is null
        // on the C# side when unset.
        var f = tw.Frame;
        return new(
            Id: new ForeignWindowId(tw.WindowId),
            Title: tw.Title,
            AppId: appId,
            IsMinimized: tw.IsMinimized,
            IsFocused: tw.IsFocused,
            Bounds: f is null ? default : new PalRect(f.X, f.Y, f.Width, f.Height),
            IconPng: icon.Length == 0 ? null : icon,
            IsAppPresence: tw.IsAppPresence,
            BundleId: string.IsNullOrEmpty(tw.AppBundleId) ? null : tw.AppBundleId,
            IsFullscreen: tw.IsFullscreen,
            IsOnOtherSpace: tw.IsOnOtherSpace,
            // Whitespace is as dead as empty — a blank path must not reach the verb (bevel-4nct review).
            BundlePath: string.IsNullOrWhiteSpace(tw.AppBundlePath) ? null : tw.AppBundlePath,
            // Attention stamp (bevel-qb19): 0 = none, otherwise unix ms of the
            // appear/title-change that requested attention while backgrounded. proto uint64 → long:
            // clamp at the boundary — a value above Int64.MaxValue saturates instead of wrapping
            // negative (which the VM's strictly-newer (>) compare would treat as ancient).
            AttentionAt: tw.AttentionAt > (ulong)long.MaxValue ? long.MaxValue : (long)tw.AttentionAt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
    }
}