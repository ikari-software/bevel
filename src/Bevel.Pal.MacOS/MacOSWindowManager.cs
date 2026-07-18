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
    private HashSet<string> _knownWindowIds = new();
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
        IClock clock, TimeSpan streamRetryInterval)
    {
        _helperLifecycle = helperLifecycle;
        _logger = logger;
        _clock = clock;
        _streamRetryInterval = streamRetryInterval;
    }

    // ── IWindowManager ──────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
    {
        if (_disposed) return Array.Empty<ForeignWindow>();

        try
        {
            var ws = GetWindowClient();
            var reply = await ws.ListWindowsAsync(new ListWindowsRequest(),
                headers: AuthHeader(), cancellationToken: ct);
            return reply.Windows.Select(Map).ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("WindowService unavailable: {Message}", ex.Message);
            return Array.Empty<ForeignWindow>();
        }
    }

    public async Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        await ws.ActivateAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: ct);
    }

    public async Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        await ws.MinimizeAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: ct);
    }

    public async Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        await ws.RestoreAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: ct);
    }

    public async Task CloseAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var ws = GetWindowClient();
        await ws.CloseAsync(new WindowRef { WindowId = id.Value },
            headers: AuthHeader(), cancellationToken: ct);
    }

    public async Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        if (_disposed) return null;
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
                headers: AuthHeader(), cancellationToken: ct);
            return reply.Png.IsEmpty ? null : reply.Png.ToByteArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // helper down / capture unavailable → caller shows no preview (but honor cancellation)
        }
    }

    public async Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        try
        {
            var ws = GetWindowClient();
            await ws.RepositionAsync(new RepositionRequest
            {
                WindowId = id.Value,
                Target = new PixelRect { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height },
            }, headers: AuthHeader(), cancellationToken: ct);
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
                var ws = GetWindowClient();
                using var call = ws.Changes(new ChangesRequest(), headers: AuthHeader(), cancellationToken: ct);

                await foreach (var change in call.ResponseStream.ReadAllAsync(ct))
                {
                    var w = change.Window is null ? null : Map(change.Window);
                    switch (change.Kind)
                    {
                        case WindowChange.Types.Kind.Snapshot:
                            if (w is not null)
                            {
                                _knownWindowIds.Add(w.Id.Value);
                                WindowChanged?.Invoke(this, w);
                            }
                            break;
                        case WindowChange.Types.Kind.Opened:
                            if (w is not null)
                            {
                                _knownWindowIds.Add(w.Id.Value);
                                WindowOpened?.Invoke(this, w);
                            }
                            break;
                        case WindowChange.Types.Kind.Closed:
                            if (w is not null) _knownWindowIds.Remove(w.Id.Value);
                            if (w is not null) WindowClosed?.Invoke(this, w);
                            break;
                        case WindowChange.Types.Kind.Focused:
                            if (w is not null)
                            {
                                WindowChanged?.Invoke(this, w);
                                if (w.IsFocused)
                                    ForegroundChanged?.Invoke(this, w);
                            }
                            break;
                        case WindowChange.Types.Kind.TitleChanged:
                        case WindowChange.Types.Kind.Moved:
                        case WindowChange.Types.Kind.Minimized:
                        case WindowChange.Types.Kind.Deminimized:
                            if (w is not null) WindowChanged?.Invoke(this, w);
                            break;
                    }
                }
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

    // ── Helpers ─────────────────────────────────────────────────────────

    private WindowService.WindowServiceClient GetWindowClient()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacOSWindowManager));
        var channel = _helperLifecycle.Client.GetChannel();
        if (channel is null) throw new InvalidOperationException("Helper not connected.");
        return new WindowService.WindowServiceClient(channel);
    }

    private Grpc.Core.Metadata AuthHeader()
        => HelperClient.BuildAuthMetadata(_helperLifecycle.NonceToken, "window");

    private static ForeignWindow Map(TaskbarWindow tw)
    {
        // AppId is only ever used as the taskbar button's label fallback (when the
        // window has no title), so prefer the friendly app name ("Google Chrome")
        // over the bundle id ("com.google.Chrome"). proto3 strings default to ""
        // (never null), so fall back explicitly on empty.
        var appId = string.IsNullOrEmpty(tw.AppName) ? tw.AppBundleId : tw.AppName;
        var icon = tw.AppIconPng.ToByteArray();
        return new(
            Id: new ForeignWindowId(tw.WindowId),
            Title: tw.Title,
            AppId: appId,
            IsMinimized: tw.IsMinimized,
            IsFocused: tw.IsFocused,
            Bounds: new PalRect(tw.Frame.X, tw.Frame.Y, tw.Frame.Width, tw.Frame.Height),
            IconPng: icon.Length == 0 ? null : icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
    }
}