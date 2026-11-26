using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's <see cref="IWindowManager"/> that is really a thin client of the shell-core owner
/// (bevel-gww.3): the core holds the single window projection (fed by the one Swift-helper stream)
/// and pushes deltas here, which this adapter re-raises as the ordinary window events the taskbar's
/// ShellModel already consumes. Window actions become core requests. So the split taskbar never
/// touches the helper or runs its own reconcile poll — that all lives in the core.
///
/// <para>Outbound resilience: a window action issued while the core link is down (or one whose send
/// races the drop) is held in a small bounded queue and replayed FIFO on reconnect, so a click
/// during the gap isn't a dead click — it just applies a beat late. Queries (Enumerate) are NOT
/// queued: a stale enumeration is worthless, and the reconcile re-runs after reconnect anyway.
/// Zoom is the one action that is never queued: a replayed zoom could hit a recycled window id
/// (the wrong window) after reconnect.</para>
/// </summary>
public sealed class ShellCoreWindowManager : IWindowManager, IProjectedWindowSource
{
    // Small, best-effort replay buffer: keep the last few actions across a blip, drop the oldest past
    // the cap, and never replay an action so old the user has moved on.
    private const int MaxQueuedCommands = 32;
    private static readonly TimeSpan ReplayTtl = TimeSpan.FromSeconds(10);

    private readonly ShellCoreClient _core;
    private readonly object _queueLock = new();
    private readonly Queue<QueuedCommand> _replayQueue = new();
    private int _draining;

    /// <summary>Every window this adapter has raised and not yet closed (bevel-wheq), so a WindowSnapshot can
    /// close what it omits. Shared bookkeeping with the tray adapter; it never raises events itself.</summary>
    private readonly ProjectionMirror<ForeignWindow> _known = new(w => w.Id.Value);

    private readonly record struct QueuedCommand(CoreCommand Command, DateTime EnqueuedUtc);

    public ShellCoreWindowManager(ShellCoreClient core)
    {
        _core = core;
        _core.EventReceived += OnCoreEvent;
        _core.ConnectionChanged += OnConnectionChanged;
    }

    // bevel-mhvu: the core FORWARDs its window manager's capabilities on the WindowSnapshot; this
    // proxy reports them verbatim. The literal stays only as the pre-snapshot fallback.
    private volatile Capabilities? _forwarded;
    public Capabilities Capabilities => _forwarded ??
        new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);

    /// <summary>Test seam: awaited after the pull's reply arrives and before it is merged — lets a test land a
    /// close delta deterministically in that window.</summary>
    internal Func<Task>? BeforePullMergeForTest;

    // internal = test seam: tests feed synthetic CoreEvents without a live server.
    internal void OnCoreEvent(CoreEvent e)
    {
        switch (e.Kind)
        {
            case CoreEventKind.WindowSnapshot:
                if (e.WindowCapabilities is { } caps) _forwarded = caps;   // bevel-mhvu: forward verbatim
                if (e.Windows is null) break;
            {
                // (fall through to the snapshot merge below)

                // A snapshot is authoritative (bevel-wheq): close what it omits, then (re)open what it lists.
                // Opens alone left stale buttons for windows that closed while the link was down. ShellModel
                // drops its foreground claim on a WindowClosed for the focused id, so a closed foreground
                // window leaves nothing pressed; a focused window in the snapshot re-claims it on its Opened.
                var gone = _known.ApplySnapshot(e.Windows);
                foreach (var g in gone)
                    WindowClosed?.Invoke(this, g);
                foreach (var w in e.Windows)
                    WindowOpened?.Invoke(this, w);
                break;
            }
            // Every non-close kind makes ShellModel show a button for the window (it upserts), even for an id it
            // has never seen — so every one of them is tracked, or a later snapshot could not close it.
            case CoreEventKind.WindowOpened when e.Window is { } wo:
                _known.Upsert(wo);
                WindowOpened?.Invoke(this, wo);
                break;
            case CoreEventKind.WindowClosed when e.Window is { } wc:
                _known.Remove(wc);
                WindowClosed?.Invoke(this, wc);
                break;
            case CoreEventKind.WindowChanged when e.Window is { } wch:
                _known.Upsert(wch);
                WindowChanged?.Invoke(this, wch);
                break;
            case CoreEventKind.WindowForeground when e.Window is { } wf:
                _known.Upsert(wf);
                ForegroundChanged?.Invoke(this, wf);
                break;
            // App-environment events are handled by ShellCoreAppEnvironment (same connection).
        }
    }

    /// <summary>The live window list (ShellModel's reconcile pull). Its result is tracked like a delta so a
    /// later snapshot can close those windows too, minus any window closed while the pull was in flight — the
    /// pull is older than that close, and ShellModel reconciles to the returned list (bevel-wheq). Transport
    /// failures still propagate: the caller treats them as "try again next tick".</summary>
    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
    {
        var start = _known.BeginPull();
        try
        {
            var windows = (await _core.SendAsync(new CoreCommand(CoreCommandKind.EnumerateWindows), ct)
                .ConfigureAwait(false)).Windows ?? Array.Empty<ForeignWindow>();
            if (BeforePullMergeForTest is { } gate) await gate().ConfigureAwait(false);
            return _known.MergePull(start, windows);
        }
        finally
        {
            _known.EndPull();
        }
    }

    /// <summary>The core's own projection, icons omitted (bevel-ougs) — no helper enumeration behind
    /// it. See <see cref="IProjectedWindowSource"/> for why this is NOT a drop-in for
    /// <see cref="EnumerateAsync"/>: a reconcile that polled this would only confirm the same delta
    /// stream it exists to check.</summary>
    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateProjectedAsync(CancellationToken ct = default)
        => (await _core.SendAsync(new CoreCommand(CoreCommandKind.EnumerateWindowsProjected), ct)
                .ConfigureAwait(false)).Windows
           ?? Array.Empty<ForeignWindow>();

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Activate, id, ct);
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Minimize, id, ct);
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Restore, id, ct);
    public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.RestoreAndActivate, id, ct);
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Close, id, ct);
    public Task ZoomAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Zoom, id, ct);
    public Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default) =>
        Send(new CoreCommand(CoreCommandKind.TerminateApp, AppIdOrPath: bundleId, Force: force), ct);

    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null,
        CancellationToken ct = default) =>
        Send(new CoreCommand(CoreCommandKind.Reposition, WindowId: id.Value, Bounds: bounds,
            ExpectedBounds: expected), ct);

    /// <summary>Query (like Enumerate): proxy the capture to the core and return the PNG, or null if the
    /// link is down / core rejects / capture unavailable. Not queued — a stale thumbnail is worthless.</summary>
    public async Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        if (!_core.IsConnected) return null;
        // Intrinsic deadline (bevel-1275): the core self-bounds its own helper capture at ~3s, but a
        // wedged IPC transport to the core would otherwise hang this call for as long as the caller
        // allowed (the taskbar-model path passes no timeout). Bound it a beat longer than the core's own
        // deadline so the core's null fallback normally wins first; on our deadline we return null → the
        // caller shows the static app icon. Only a caller-driven cancellation propagates.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CaptureRpcTimeout);
        try
        {
            var r = await _core.SendAsync(
                new CoreCommand(CoreCommandKind.CaptureWindow, WindowId: id.Value, MaxWidth: maxWidth, MaxHeight: maxHeight),
                cts.Token).ConfigureAwait(false);
            return r.Ok ? r.Png : null;
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();  // caller cancelled → honor it
            return null;                        // intrinsic timeout / transport failure → no preview
        }
    }

    /// <summary>Client-side cap on a capture round-trip to the core, a beat above the core's own ~3s
    /// helper-capture deadline so the core's null fallback normally returns first (bevel-1275).</summary>
    private static readonly TimeSpan CaptureRpcTimeout = TimeSpan.FromSeconds(5);

    private Task Command(CoreCommandKind kind, ForeignWindowId id, CancellationToken ct) =>
        Send(new CoreCommand(kind, WindowId: id.Value), ct);

    /// <summary>Send a window action, or queue it for replay if the link is down or drops mid-send —
    /// so the action isn't lost as a dead click. A core-side rejection (Ok=false) is a real failure
    /// and still throws.</summary>
    private async Task Send(CoreCommand cmd, CancellationToken ct)
    {
        if (!_core.IsConnected)
        {
            Enqueue(cmd);
            return;
        }
        try
        {
            var r = await _core.SendAsync(cmd, ct).ConfigureAwait(false);
            if (!r.Ok)
                throw new InvalidOperationException($"shell-core {cmd.Kind} failed: {r.Error}");
        }
        catch (IOException)
        {
            // Transport dropped before/mid-send — hold for replay instead of surfacing a dead click.
            Enqueue(cmd);
        }
        catch (InvalidOperationException) when (!_core.IsConnected)
        {
            // "Not connected" raced the disconnect detection — same treatment.
            Enqueue(cmd);
        }
    }

    // bevel-kpkx review: Zoom is NOT replayable — a queued zoom replayed after reconnect could hit a
    // recycled window id (the wrong window entirely). A dropped zoom click is recoverable by the
    // user; a zoom landing on the wrong window is not. Everything else stays replayable.
    private static bool IsReplayable(CoreCommandKind kind) => kind != CoreCommandKind.Zoom;

    private void Enqueue(CoreCommand cmd)
    {
        if (!IsReplayable(cmd.Kind))
        {
            Console.Error.WriteLine($"[shellcore] dropped {cmd.Kind} (not replayable; link down)");
            return;
        }
        lock (_queueLock)
        {
            while (_replayQueue.Count >= MaxQueuedCommands)
                _replayQueue.Dequeue(); // bounded: shed the oldest
            _replayQueue.Enqueue(new QueuedCommand(cmd, DateTime.UtcNow));
        }
        Console.Error.WriteLine($"[shellcore] queued {cmd.Kind} for replay (link down)");
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (connected)
            _ = DrainReplayQueueAsync();
    }

    /// <summary>Replay queued actions FIFO after reconnect. A single drainer at a time; an item that
    /// times out is dropped, and if the link dies again mid-drain the remainder stays queued for the
    /// next reconnect (peek-then-remove-on-success preserves order and timestamps).</summary>
    private async Task DrainReplayQueueAsync()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 1) return;
        try
        {
            var replayed = 0;
            while (true)
            {
                QueuedCommand item;
                lock (_queueLock)
                {
                    if (_replayQueue.Count == 0) break;
                    item = _replayQueue.Peek();
                }

                if (DateTime.UtcNow - item.EnqueuedUtc > ReplayTtl)
                {
                    lock (_queueLock) { if (_replayQueue.Count > 0) _replayQueue.Dequeue(); }
                    continue; // too stale to matter
                }

                try
                {
                    var r = await _core.SendAsync(item.Command).ConfigureAwait(false);
                    if (!r.Ok)
                        Console.Error.WriteLine($"[shellcore] replayed {item.Command.Kind} rejected: {r.Error}");
                }
                catch
                {
                    return; // link died again — leave this and the rest queued for the next reconnect
                }

                lock (_queueLock) { if (_replayQueue.Count > 0) _replayQueue.Dequeue(); }
                replayed++;
            }
            if (replayed > 0)
                Console.Error.WriteLine($"[shellcore] replayed {replayed} queued command(s) after reconnect");
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }
    }

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}
