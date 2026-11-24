namespace Bevel.App.Uninstall;

internal enum UninstallActionKind
{
    StopShell,
    RestoreDock,
    DisableLoginItem,
    UnregisterBundle,
    ResetTcc,
    DeletePath,
    PrintHint,
}

/// <summary>One uninstall step: what to do (<see cref="Kind"/>), what it applies to
/// (<see cref="Target"/> — a path, pid file, bundle id list, …) and a human-readable line.</summary>
internal sealed record UninstallAction(UninstallActionKind Kind, string Target, string Description);

/// <summary>
/// The machine paths the uninstaller touches, abstracted so tests root everything in a temp dir —
/// <c>BevelConfigDir.Path</c> resolves the developer's REAL config dir and throws in a test host.
/// </summary>
internal sealed class UninstallEnvironment
{
    public const string BundleId = "pl.ikari.bevel";
    public const string HelperBundleId = "pl.ikari.bevel.helper";

    /// <summary><c>~/.config/bevel</c>: settings.db, shell.lock(+pid), sockets.</summary>
    public required string ConfigDir { get; init; }
    /// <summary><c>~/.bevel/dock-claimed.json</c>: the Dock autohide value Bevel captured.</summary>
    public required string DockMarkerFile { get; init; }
    /// <summary>Per-user runtime root (tmp on macOS/Linux): bevel-core/-filers/-health/-helper + logs.</summary>
    public required string RuntimeRoot { get; init; }
    /// <summary>The running .app bundle (for <c>lsregister -u</c>); empty when run via dotnet.</summary>
    public required string AppBundlePath { get; init; }
    public bool KeepSettings { get; init; }

    /// <summary>The launcher's pid file — <c>shell.lock.pid</c> beside the lock
    /// (<see cref="Supervision.ShellInstanceLock"/>).</summary>
    public string PidFile => Path.Combine(ConfigDir, "shell.lock.pid");

    public static UninstallEnvironment Production(bool keepSettings)
    {
        var exe = Environment.ProcessPath;
        // …/Bevel.app/Contents/MacOS/Bevel → the bundle is three levels up; anything else
        // (dotnet run, tests) has no bundle to unregister.
        var bundle = string.IsNullOrEmpty(exe) ? "" :
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", ".."));
        if (!bundle.EndsWith(".app", StringComparison.Ordinal)) bundle = "";
        return new UninstallEnvironment
        {
            ConfigDir = Bevel.Core.BevelConfigDir.Path,
            DockMarkerFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bevel", "dock-claimed.json"),
            RuntimeRoot = BevelRuntimeDir.Root,
            AppBundlePath = bundle,
            KeepSettings = keepSettings,
        };
    }
}

/// <summary>
/// Builds the ordered uninstall plan (ENG-040, bevel-q3l). PURE apart from reading which optional
/// markers exist (dock marker, bundle path) — every path comes from the
/// <see cref="UninstallEnvironment"/>, so tests run against a temp dir. Order: stop the running
/// shell first (so nothing re-writes state while we clean), then hand back machine-level
/// registrations, then delete what Bevel wrote, then the manual last step. The app never deletes
/// its own bundle.
/// </summary>
internal static class UninstallPlan
{
    public static IReadOnlyList<UninstallAction> Build(UninstallEnvironment env)
    {
        var actions = new List<UninstallAction>
        {
            new(UninstallActionKind.StopShell, env.PidFile,
                "Stop the running shell (SIGTERM the launcher pid from shell.lock.pid, wait up to 10s — never SIGKILL)"),
        };
        if (File.Exists(env.DockMarkerFile))
            actions.Add(new(UninstallActionKind.RestoreDock, env.DockMarkerFile,
                "Restore the macOS Dock autohide value Bevel captured in the marker, then remove the marker"));
        actions.Add(new(UninstallActionKind.DisableLoginItem, "Bevel", "Remove Bevel from Login Items"));
        if (env.AppBundlePath.Length > 0)
            actions.Add(new(UninstallActionKind.UnregisterBundle, env.AppBundlePath,
                "lsregister -u the bundle: drop the bevel:// and public.folder LaunchServices registration"));
        actions.Add(new(UninstallActionKind.ResetTcc,
                $"{UninstallEnvironment.BundleId} {UninstallEnvironment.HelperBundleId}",
                "tccutil reset All for both bundle ids: clear the Accessibility / Screen Recording grants"));
        if (!env.KeepSettings)
            actions.Add(new(UninstallActionKind.DeletePath, env.ConfigDir,
                "Delete the config dir (settings.db, shell.lock, sockets)"));
        var bevelDir = Path.GetDirectoryName(env.DockMarkerFile);
        if (!string.IsNullOrEmpty(bevelDir))
            actions.Add(new(UninstallActionKind.DeletePath, bevelDir, "Delete ~/.bevel (dock-claimed marker)"));
        foreach (var d in new[] { "bevel-core", "bevel-filers", "bevel-health", "bevel-helper" })
            actions.Add(new(UninstallActionKind.DeletePath, Path.Combine(env.RuntimeRoot, d),
                $"Delete the {d} runtime dir (sockets, token, heartbeats, icon pool)"));
        actions.Add(new(UninstallActionKind.DeletePath, Path.Combine(env.RuntimeRoot, "bevel-restart.log"),
            "Delete the restart diag log"));
        actions.Add(new(UninstallActionKind.DeletePath, Path.Combine(env.RuntimeRoot, "bevel-filer-*.log"),
            "Delete per-filer stderr logs"));
        actions.Add(new(UninstallActionKind.PrintHint, "Bevel.app",
            "Now move Bevel.app to the Trash — the app never deletes its own bundle"));
        return actions;
    }
}
