using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U6 (bevel-ncfp.6) lands process↔window correlation (running apps), Start-menu enumeration
/// (installed apps), launch/activate, and app icons. Bootstrap stub for now.</summary>
public sealed class WindowsAppEnvironment : IAppEnvironment
{
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Array.Empty<InstalledApp>());

    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
}
