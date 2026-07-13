using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's <see cref="IAppEnvironment"/> that proxies to the shell-core owner (bevel-gww.3),
/// sharing the same <see cref="ShellCoreClient"/> connection as the window manager. The core runs the
/// single set of <c>/Applications</c> watchers and NSWorkspace queries; this adapter turns the
/// installed/running-app queries and launches into core requests and re-raises the core's
/// app-launched/terminated deltas as the events the Start menu / task list consume.
/// </summary>
public sealed class ShellCoreAppEnvironment : IAppEnvironment
{
    private readonly ShellCoreClient _core;

    public ShellCoreAppEnvironment(ShellCoreClient core)
    {
        _core = core;
        _core.EventReceived += OnCoreEvent;
    }

    private void OnCoreEvent(CoreEvent e)
    {
        switch (e.Kind)
        {
            case CoreEventKind.AppLaunched when e.App is { } a:
                AppLaunched?.Invoke(this, a);
                break;
            case CoreEventKind.AppTerminated when e.App is { } a:
                AppTerminated?.Invoke(this, a);
                break;
            // InstalledAppsSnapshot carries no IAppEnvironment event (consumers pull via
            // EnumerateInstalledAppsAsync); a round-trip answers that pull — simple and correct.
        }
    }

    public async ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => (await _core.SendAsync(new CoreCommand(CoreCommandKind.EnumerateInstalledApps), ct).ConfigureAwait(false))
           .InstalledApps ?? Array.Empty<InstalledApp>();

    public async ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => (await _core.SendAsync(new CoreCommand(CoreCommandKind.GetRunningApps), ct).ConfigureAwait(false))
           .RunningApps ?? Array.Empty<RunningApp>();

    public async Task LaunchAsync(string appIdOrPath, CancellationToken ct = default)
    {
        var r = await _core.SendAsync(new CoreCommand(CoreCommandKind.LaunchApp, AppIdOrPath: appIdOrPath), ct)
            .ConfigureAwait(false);
        if (!r.Ok)
            throw new InvalidOperationException($"shell-core LaunchApp failed: {r.Error}");
    }

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
}
