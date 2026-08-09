using Bevel.Interop;
using Bevel.Pal.Abstractions;

namespace Bevel.App;

/// <summary>
/// The real <see cref="IProgramSurface"/> behind the automation command model: adapts the shell's
/// <see cref="IAppEnvironment"/> — the SAME installed-app source and launcher the Start menu uses — so
/// <c>bevelctl launch</c> / <c>bevel://launch</c> and the <c>programs</c> query reach exactly the
/// programs a person sees and launches in the UI (agent-native parity, bevel-0zq). Maps the shell's
/// <see cref="InstalledApp"/> down to the command model's PAL-free <see cref="ProgramInfo"/>.
/// </summary>
public sealed class AppEnvironmentProgramSurface : IProgramSurface
{
    private readonly IAppEnvironment _appEnv;

    public AppEnvironmentProgramSurface(IAppEnvironment appEnv) => _appEnv = appEnv;

    public async Task<IReadOnlyList<ProgramInfo>> ListProgramsAsync(CancellationToken ct)
    {
        var apps = await _appEnv.EnumerateInstalledAppsAsync(ct).ConfigureAwait(false);
        var list = new List<ProgramInfo>(apps.Count);
        foreach (var a in apps)
            list.Add(new ProgramInfo(a.AppId, a.DisplayName));
        return list;
    }

    public Task LaunchAsync(string appId, CancellationToken ct) => _appEnv.LaunchAsync(appId, ct);
}
