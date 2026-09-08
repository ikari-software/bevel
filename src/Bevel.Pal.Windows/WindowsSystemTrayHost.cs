using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U4 (bevel-ncfp.4, HIGH-RISK spike) decides mirror-vs-own-app for the Windows notification
/// area. Bootstrap stub reports no source (like EmptySystemTrayHost).</summary>
public sealed class WindowsSystemTrayHost : ISystemTrayHost
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}
