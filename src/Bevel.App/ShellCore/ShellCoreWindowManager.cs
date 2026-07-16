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
/// queued: a stale enumeration is worthless, and the reconcile re-runs after reconnect anyway.</para>
/// </summary>
public sealed class ShellCoreWindowManager : IWindowManager
{
    // Small, best-effort replay buffer: keep the last few actions across a blip, drop the oldest past
    // the cap, and never replay an action so old the user has moved on.
    private const int MaxQueuedCommands = 32;
    private static readonly TimeSpan ReplayTtl = TimeSpan.FromSeconds(10);

    private readonly ShellCoreClient _core;
    private readonly object _queueLock = new();
    private readonly Queue<QueuedCommand> _replayQueue = new();
    private int _draining;

    private readonly record struct QueuedCommand(CoreCommand Command, DateTime EnqueuedUtc);

    public ShellCoreWindowManager(ShellCoreClient core)
    {
        _core = core;
        _core.EventReceived += OnCoreEvent;
        _core.ConnectionChanged += OnConnectionChanged;
    }

    // Reposition is proxied to the core (which owns the helper's reposition capability), so advertise
    // it — the WorkAreaMitigator feature-detects on this flag.
    public Capabilities Capabilities { get; } =
        new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);

    private void OnCoreEvent(CoreEvent e)
    {
        switch (e.Kind)
        {
            case CoreEventKind.WindowSnapshot when e.Windows is not null:
                // Replay the snapshot as Opened events — ShellModel upserts idempotently by id.
                foreach (var w in e.Windows)
                    WindowOpened?.Invoke(this, w);
                break;
            case CoreEventKind.WindowOpened when e.Window is { } wo:
                WindowOpened?.Invoke(this, wo);
                break;
            case CoreEventKind.WindowClosed when e.Window is { } wc:
                WindowClosed?.Invoke(this, wc);
                break;
            case CoreEventKind.WindowChanged when e.Window is { } wch:
                WindowChanged?.Invoke(this, wch);
                break;
            case CoreEventKind.WindowForeground when e.Window is { } wf:
                ForegroundChanged?.Invoke(this, wf);
                break;
            // App-environment events are handled by ShellCoreAppEnvironment (same connection).
        }
    }

    public async ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => (await _core.SendAsync(new CoreCommand(CoreCommandKind.EnumerateWindows), ct).ConfigureAwait(false)).Windows
           ?? Array.Empty<ForeignWindow>();

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Activate, id, ct);
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Minimize, id, ct);
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Restore, id, ct);
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Command(CoreCommandKind.Close, id, ct);

    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) =>
        Send(new CoreCommand(CoreCommandKind.Reposition, WindowId: id.Value, Bounds: bounds), ct);

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

    private void Enqueue(CoreCommand cmd)
    {
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
