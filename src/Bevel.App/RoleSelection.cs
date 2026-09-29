namespace Bevel.App;

/// <summary>
/// Which shell surface this process hosts. The shell ALWAYS runs split: each surface is its own
/// process, launched from the same executable with <c>--role=taskbar|filer|desktop|core</c>
/// (mirrors the <c>--pal=</c> switch) under a supervising <c>--role=launcher</c>. An argument-less
/// launch is the launcher, which spawns the surface processes — there is no single-process mode.
///
/// Splitting buys crash isolation (a Filer crash doesn't take down the taskbar) and lets each
/// process instantiate ONLY the services it resolves: MS.DI singletons are lazy, so a non-taskbar
/// role that never resolves <c>IWindowManager</c>/<c>IAppEnvironment</c> never constructs the helper
/// client or the <c>/Applications</c> filesystem watchers — no per-process duplication of that work.
/// </summary>
public enum ShellRole
{
    /// <summary>The taskbar: Start menu, window buttons, clock. The only role that drives window management.</summary>
    Taskbar,

    /// <summary>A file-manager / Filer window.</summary>
    Filer,

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
/// Resolves the shell role from the command line: <c>--role=taskbar|filer|desktop|core|launcher</c>.
/// Defaults to <see cref="ShellRole.Launcher"/> so an argument-less launch is the split launcher that
/// spawns the surface processes. Mirrors <see cref="PalSelector"/>'s <c>=</c>-form parsing; unknown
/// values fall back to the launcher.
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
                "filer" or "files" or "filemanager" => ShellRole.Filer,
                "desktop" => ShellRole.Desktop,
                "core" => ShellRole.Core,
                "launcher" or "supervisor" or "boot" => ShellRole.Launcher,
                _ => ShellRole.Launcher,
            };
        }

        return ShellRole.Launcher;
    }
}
