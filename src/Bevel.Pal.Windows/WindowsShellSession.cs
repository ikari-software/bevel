using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U5 (bevel-ncfp.5, HIGH-RISK spike) lands per-user HKCU Winlogon\Shell set-as-shell,
/// run-at-login, and ExitWindowsEx. Bootstrap stub for now.</summary>
public sealed class WindowsShellSession : IShellSession
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task RegisterAsShellAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task UnregisterAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler? SessionChanged;
}
