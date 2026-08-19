using Bevel.Core;
using Bevel.Desktop;
using Bevel.FileManager;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Bevel.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bevel.App;

/// <summary>
/// DI composition helpers. This is the only place concrete PALs are named (ARCH-03/DI-02).
///
/// <para>Also the role-aware home of <see cref="Bevel.Core.ISettingsService"/> (core-owns-settings,
/// bevel-6nve): Core (and the Fake PAL) binds the real DB-backed <c>SettingsService</c> — the sole
/// opener + writer of settings.db; Taskbar/Explorer/Desktop bind <c>RemoteSettingsService</c>, a peer that
/// reads the core's pushed snapshot and sends changed-keys merge patches to the core over a shell-core
/// client. Taskbar reuses its existing client; Explorer/Desktop add a settings-only client (their
/// window/app/tray stay direct-PAL). The Core builds NO client — it is the server.</para>
/// </summary>
public static class CompositionRoot
{
    // Shared icon-pool geometry (bevel-gww.6). Co-located with the shell-core runtime dir so a
    // supervisor cleaning that dir clears the pool too. 512 slots × up to 96×96 BGRA (covers 48pt@2x
    // Retina) ≈ 19 MB, sparse: only pages for actually-published icons ever become resident.
    private static string IconPoolPath => Path.Combine(Path.GetTempPath(), "bevel-core", "icons.pool");
    private const int IconPoolSlotCapacity = 512;
    private const int IconPoolMaxBgraBytes = 96 * 96 * 4;

    /// <summary>
    /// Registers the selected concrete PAL's implementation of every abstraction. <paramref name="role"/>
    /// gates only what must be started EAGERLY (the helper hosted service): the plain singletons stay
    /// registered for every role but are lazy, so a role that never resolves them costs nothing.
    /// </summary>
    public static IServiceCollection AddBevelPlatform(
        this IServiceCollection services, PalKind pal, ShellRole role)
    {
        return pal switch
        {
            PalKind.MacOS => services.AddMacOSPal(role),
            _ => services.AddFakePal(),
        };
    }

    private static IServiceCollection AddFakePal(this IServiceCollection services)
    {
        services.AddSingleton<IWindowManager, Pal.Fake.FakeWindowManager>();
        services.AddSingleton<ISystemTrayHost, Pal.Fake.FakeSystemTrayHost>();
        services.AddSingleton<IDesktopEnvironment, Pal.Fake.FakeDesktopEnvironment>();
        services.AddSingleton<IShellSession, Pal.Fake.FakeShellSession>();
        services.AddSingleton<IFileOperations, Pal.Fake.FakeFileOperations>();
        services.AddSingleton<IIconProvider, Pal.Fake.FakeIconProvider>();
        services.AddSingleton<IAppEnvironment, Pal.Fake.FakeAppEnvironment>();
        services.AddSingleton<IFileOpener, Pal.Fake.FakeFileOpener>();
        services.AddSingleton<IPermissionBroker, Pal.Fake.FakePermissionBroker>();
        services.AddSingleton<IAudioPlayback, Pal.Fake.FakeAudioPlayback>();
        services.AddSingleton<IDockController, Pal.Fake.FakeDockController>();
        services.AddSingleton<IShellConnectionStatus, AlwaysConnectedShellStatus>();
        services.AddSingleton<ITabProvider, Pal.Fake.FakeTabProvider>();
        // The Fake PAL is the single-process dev/test shell, so it owns settings.db directly through the
        // real service — never a shell-core peer (core-owns-settings, bevel-6nve).
        services.AddSingleton<Bevel.Core.ISettingsService, Bevel.Core.SettingsService>();
        return services;
    }

    private static IServiceCollection AddMacOSPal(this IServiceCollection services, ShellRole role)
    {
        // In-process macOS PAL services (AppKit/NSWorkspace in-proc) — every role uses these directly;
        // they're lazy, so a role that never resolves one never constructs it.
        services.AddSingleton<IDesktopEnvironment, Pal.MacOS.MacOSDesktopEnvironment>();
        services.AddSingleton<IShellSession, Pal.MacOS.MacOSShellSession>();
        services.AddSingleton<IFileOperations, Pal.MacOS.MacOSFileOperations>();

        // Icons go through the shared, memory-mapped BGRA pool (bevel-gww.6): each icon is rendered once
        // and published to a file the other role processes map read-only, so a UI process never
        // re-decodes an icon the owner already has. The pool is SINGLE-WRITER — only the shell-core
        // owner publishes; the UI roles are readers (miss -> private render). The pool
        // is a container-owned singleton (disposed with the container); the decorator just borrows it.
        // The isWriter flag is now ALSO passed to the pool itself so a reader role opens the file
        // read-only and never creates/resizes/inits it (ce-review bevel-lha4).
        var poolIsWriter = role is ShellRole.Core;
        services.AddSingleton(_ => MmfBgraPool.CreateOrOpen(
            IconPoolPath, IconPoolSlotCapacity, IconPoolMaxBgraBytes, isWriter: poolIsWriter));
        services.AddSingleton<IIconProvider>(sp => new PooledIconProvider(
            new Pal.MacOS.MacOSIconProvider(),
            sp.GetRequiredService<MmfBgraPool>(),
            isWriter: poolIsWriter));

        services.AddSingleton<IPermissionBroker, Pal.MacOS.MacOSPermissionBroker>();
        // Opening a document is a purely local `open`(1) spawn — no shell-core proxy, every role direct.
        services.AddSingleton<IFileOpener, Pal.MacOS.MacOSFileOpener>();
        services.AddSingleton<IAudioPlayback, Pal.MacOS.MacOSAudioPlayback>();
        services.AddSingleton<IDockController, Pal.MacOS.MacOSDockController>();
        services.AddSingleton<IVolumeLabelSource, Pal.MacOS.MacOSVolumeLabelSource>();
        // Tabs are enumerated by talking Apple Events to the target app — purely local (an osascript
        // child), so like IFileOpener every role gets the direct implementation, no core proxy.
        services.AddSingleton<ITabProvider, Pal.MacOS.MacOSTabProvider>();

        // Window management + app environment: the single-source-of-truth split. In the taskbar
        // process these are shell-core CLIENTS (one UDS connection to the core, which owns the helper
        // stream + the /Applications watchers). In the Core role and the (lazy, unused) explorer/desktop
        // roles they are the DIRECT macOS implementations.
        if (role is ShellRole.Taskbar)
        {
            services.AddSingleton(_ => ShellCore.ShellCoreEndpoint.CreateClient());
            services.AddSingleton<IWindowManager, ShellCore.ShellCoreWindowManager>();
            services.AddSingleton<IAppEnvironment, ShellCore.ShellCoreAppEnvironment>();
            // The one core connection IS the link-health source the taskbar's tray indicator tracks.
            services.AddSingleton<IShellConnectionStatus>(sp => sp.GetRequiredService<ShellCore.ShellCoreClient>());
            // The split taskbar mirrors the tray via the shell core (bevel-m3.1.1): the core owns the
            // real TrayService stream and pushes items here, exactly like windows.
            services.AddSingleton<ISystemTrayHost, ShellCore.ShellCoreSystemTrayHost>();
            // Settings peer (core-owns-settings, bevel-6nve): reads/writes flow through the core so the
            // taskbar never opens settings.db. It gets its OWN core client (keyed "settings"), NOT the
            // shared window/app/tray client above: RemoteSettingsService connects during startup LoadAsync,
            // and sharing the tray client would make that early connect consume the core's on-connect
            // TraySnapshot before the tray adapter subscribes — leaving the split taskbar's tray empty (the
            // tray has no reconcile backstop to re-derive it). A separate client keeps each connection's
            // on-connect snapshot flowing to its own subscriber. RemoteSettingsService pulls the keyed
            // client via [FromKeyedServices("settings")]; DI owns both clients' disposal.
            services.AddKeyedSingleton<ShellCore.ShellCoreClient>("settings", (_, _) => ShellCore.ShellCoreEndpoint.CreateClient());
            services.AddSingleton<ISettingsService, ShellCore.RemoteSettingsService>();
        }
        else
        {
            services.AddSingleton<IWindowManager, Pal.MacOS.MacOSWindowManager>();
            services.AddSingleton<IAppEnvironment, Pal.MacOS.MacOSAppEnvironment>();
            // Direct tray host: talks to the helper's TrayService (needs HelperLifecycle, below).
            services.AddSingleton<ISystemTrayHost, Pal.MacOS.MacOSSystemTrayHost>();
            services.AddSingleton<Pal.MacOS.HelperLifecycle>();
            // In-process window management: no link to lose, so the indicator stays hidden.
            services.AddSingleton<IShellConnectionStatus, AlwaysConnectedShellStatus>();

            if (role is ShellRole.Core)
            {
                // The shell core is the SOLE opener + writer of settings.db (core-owns-settings,
                // bevel-6nve).
                services.AddSingleton<ISettingsService, Bevel.Core.SettingsService>();
                // Host the helper EAGERLY only where window management actually runs: the headless core.
                // Explorer/Desktop keep the singleton lazy, never started.
                services.AddHostedService(sp => sp.GetRequiredService<Pal.MacOS.HelperLifecycle>());
            }
            else
            {
                // Explorer / Desktop are settings PEERS but keep window/app/tray DIRECT-PAL (in-process).
                // Give them a settings-ONLY shell-core client (keyed "settings", the one RemoteSettingsService
                // pulls via [FromKeyedServices]) so ONLY the core opens the DB; their window manager / app
                // environment / tray above are unaffected (core-owns-settings, bevel-6nve).
                services.AddKeyedSingleton<ShellCore.ShellCoreClient>("settings", (_, _) => ShellCore.ShellCoreEndpoint.CreateClient());
                services.AddSingleton<ISettingsService, ShellCore.RemoteSettingsService>();
            }
        }

        return services;
    }

    /// <summary>Lets each feature module self-register its services (DI-01).</summary>
    public static IServiceCollection AddBevelModules(this IServiceCollection services, ShellRole role)
    {
        IModule[] modules =
        {
            new DesktopModule(),
            new TaskbarModule(),
            new FileManagerModule(),
        };

        foreach (var module in modules)
        {
            module.ConfigureServices(services);
        }

        // File > New Window (Ctrl+N): builds additional independent FileManagerWindow
        // instances sharing the VfsRoot/SettingsService singletons registered above by
        // FileManagerModule. Singleton so App and any window's NewWindow hook resolve the
        // same factory.
        services.AddSingleton<FileManagerWindowRegistry>();
        services.AddSingleton<FileManagerWindowFactory>();

        // Automation command model (08-os-interop.md §3.1 / M4): the single seam every inbound
        // surface (Apple Events, bevelctl, bevel://) funnels through. The window-coupled verbs reach a
        // file-manager surface; the filesystem verbs run on the VFS.
        services.AddSingleton<Bevel.Interop.IKnownFolders>(Bevel.Interop.SystemKnownFolders.Instance);
        // The window seam is ROLE-AWARE (bevel-e7a7). The Explorer process owns a live in-process
        // FileManagerWindow graph, so it uses FileManagerShellSurface (addresses windows by registry id).
        // Every other role — crucially the persistent TASKBAR that now hosts the socket — has no such
        // graph, so it uses SpawningShellSurface, whose open/reveal verbs spawn a --role=explorer process
        // instead of building a malformed window in-process. This is what lets bevelctl/bevel:// open &
        // reveal with NO Explorer already running.
        if (role is ShellRole.Explorer)
            services.AddSingleton<Bevel.Interop.IShellSurface, FileManagerShellSurface>();
        else
            services.AddSingleton<Bevel.Interop.IShellSurface, SpawningShellSurface>();
        services.AddSingleton<IExplorerSpawner, ProcessExplorerSpawner>();
        services.AddSingleton<Bevel.Interop.IProgramSurface, AppEnvironmentProgramSurface>();
        services.AddSingleton<Bevel.Interop.IShellAutomation, Bevel.Interop.ShellAutomation>();
        // The bevelctl + bevel:// execution core (M4-D): both surfaces parse into a ParsedCommand and
        // run it through this router → the one IShellAutomation seam. The transports that feed it (the
        // bevelctl socket, the macOS bevel:// URL-event handler) resolve this singleton.
        services.AddSingleton<Bevel.Interop.Cli.AutomationCommandRouter>();

        // Serve the bevelctl socket from the PERSISTENT host — the always-up TASKBAR (bevel-e7a7), NOT
        // the on-demand Explorer that only exists once a window is open. The taskbar has the full
        // automation DI graph; its filesystem/program verbs run directly, and its window verbs spawn an
        // Explorer via SpawningShellSurface. Exactly one process binds the fixed socket path, so there is
        // never a second server on it. A bind failure is logged, never fatal.
        if (role is ShellRole.Taskbar)
            services.AddHostedService<AutomationSocketHost>();

        return services;
    }
}
