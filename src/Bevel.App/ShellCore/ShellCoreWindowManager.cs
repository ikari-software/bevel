using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's <see cref="IWindowManager"/> that is really a thin client of the shell-core owner
/// (bevel-gww.3): the core holds the single window projection (fed by the one Swift-helper stream)
/// and pushes deltas here, which this adapter re-raises as the ordinary window events the taskbar's
/// ShellModel already consumes. Window actions become core requests. So the split taskbar never
/// touches the helper or runs its own reconcile poll — that all lives in the core.
/// </summary>
public sealed class ShellCoreWindowManager : IWindowManager
{
    private readonly ShellCoreClient _core;

    public ShellCoreWindowManager(ShellCoreClient core)
    {
        _core = core;
        _core.EventReceived += OnCoreEvent;
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
        Throwing(new CoreCommand(CoreCommandKind.Reposition, WindowId: id.Value, Bounds: bounds), ct);

    private Task Command(CoreCommandKind kind, ForeignWindowId id, CancellationToken ct) =>
        Throwing(new CoreCommand(kind, WindowId: id.Value), ct);

    private async Task Throwing(CoreCommand cmd, CancellationToken ct)
    {
        var r = await _core.SendAsync(cmd, ct).ConfigureAwait(false);
        if (!r.Ok)
            throw new InvalidOperationException($"shell-core {cmd.Kind} failed: {r.Error}");
    }

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}
