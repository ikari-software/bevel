using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Test double for <see cref="IAppEnvironment"/> that yields a fixed set of installed apps and no
/// running apps. Shared by the ShellModel reconcile test and the bound-Programs render test so the
/// two don't each carry a private copy of the same stub.
/// </summary>
internal sealed class StubAppEnvironment : IAppEnvironment
{
    private readonly IReadOnlyList<InstalledApp> _apps;
    public StubAppEnvironment(params InstalledApp[] apps) => _apps = apps;

    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_apps);
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RunningApp>>([]);
    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0067 // required by the interface; this stub never raises them
    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;

    /// <summary>Test hook: simulate an app being installed/removed by publishing a fresh list.</summary>
    public void RaiseInstalledAppsChanged(IReadOnlyList<InstalledApp> apps) => InstalledAppsChanged?.Invoke(this, apps);
#pragma warning restore CS0067
}
