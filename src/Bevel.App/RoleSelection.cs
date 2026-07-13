namespace Bevel.App;

/// <summary>
/// Which shell surface this process hosts. The shell runs either as one all-in-one process
/// (<see cref="ShellRole.All"/> — the default and the pre-split behaviour) or split so each surface
/// is its own process, launched from the same executable with
/// <c>--role=taskbar|explorer|desktop</c> (mirrors the <c>--pal=</c> switch).
///
/// Splitting buys crash isolation (an Explorer crash doesn't take down the taskbar) and lets each
/// process instantiate ONLY the services it resolves: MS.DI singletons are lazy, so a non-taskbar
/// role that never resolves <c>IWindowManager</c>/<c>IAppEnvironment</c> never constructs the helper
/// client or the <c>/Applications</c> filesystem watchers — no per-process duplication of that work.
/// </summary>
public enum ShellRole
{
    /// <summary>All surfaces in one process — the default, back-compatible single-process shell.</summary>
    All,

    /// <summary>The taskbar: Start menu, window buttons, clock. The only role that drives window management.</summary>
    Taskbar,

    /// <summary>A file-manager / Explorer window.</summary>
    Explorer,

    /// <summary>The desktop: wallpaper + icon grid, behind everything.</summary>
    Desktop,

    /// <summary>The headless shell-core owner: no window. Owns the single live window/app projection
    /// (the one Swift-helper subscription + the app watchers) and serves it to the UI roles over the
    /// shell-core IPC. Started before the UI roles by the supervisor.</summary>
    Core,

    /// <summary>The supervisor/launcher: no window. Spawns the shell-core owner and the UI role
    /// processes in order, crash-restarts them, and fans a taskbar-initiated quit/restart out to the
    /// whole set — a restart re-execs the current binary, so every process comes back on the latest
    /// build. This is the entry point for the multi-process shell.</summary>
    Launcher,
}

/// <summary>
/// Resolves the shell role from the command line: <c>--role=taskbar|explorer|desktop</c>.
/// Defaults to <see cref="ShellRole.All"/> so an argument-less launch is the classic single process.
/// Mirrors <see cref="PalSelector"/>'s <c>=</c>-form parsing; unknown values fall back to All.
/// </summary>
public static class RoleSelector
{
    public static ShellRole FromArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
                continue;

            return arg["--role=".Length..].ToLowerInvariant() switch
            {
                "taskbar" or "bar" => ShellRole.Taskbar,
                "explorer" or "files" or "filemanager" => ShellRole.Explorer,
                "desktop" => ShellRole.Desktop,
                "core" => ShellRole.Core,
                "launcher" or "supervisor" or "boot" => ShellRole.Launcher,
                _ => ShellRole.All,
            };
        }

        return ShellRole.All;
    }
}
