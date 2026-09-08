using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U3 (bevel-ncfp.3) lands EnumWindows + SetWinEventHook on a dedicated message-pump thread,
/// the full action surface, DPI-correct bounds, PrintWindow capture, and UWP/AUMID identity. Bootstrap
/// stub for now (reports unavailable; enumerate empty; user actions throw).</summary>
public sealed class WindowsWindowManager : IWindowManager
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}
