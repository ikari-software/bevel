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
        this IServiceCollection services, PalKind pal, ShellRole role = ShellRole.All)
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
        // re-decodes an icon the owner already has. The pool is SINGLE-WRITER — only the all-in-one or
        // the shell-core owner publishes; split UI roles are readers (miss -> private render). The pool
        // is a container-owned singleton (disposed with the container); the decorator just borrows it.
        services.AddSingleton(_ => MmfBgraPool.CreateOrOpen(
            IconPoolPath, IconPoolSlotCapacity, IconPoolMaxBgraBytes));
        services.AddSingleton<IIconProvider>(sp => new PooledIconProvider(
            new Pal.MacOS.MacOSIconProvider(),
            sp.GetRequiredService<MmfBgraPool>(),
            isWriter: role is ShellRole.All or ShellRole.Core));

        services.AddSingleton<IPermissionBroker, Pal.MacOS.MacOSPermissionBroker>();
        // Opening a document is a purely local `open`(1) spawn — no shell-core proxy, every role direct.
        services.AddSingleton<IFileOpener, Pal.MacOS.MacOSFileOpener>();
        services.AddSingleton<IAudioPlayback, Pal.MacOS.MacOSAudioPlayback>();
        services.AddSingleton<IDockController, Pal.MacOS.MacOSDockController>();
        services.AddSingleton<IVolumeLabelSource, Pal.MacOS.MacOSVolumeLabelSource>();

        // Window management + app environment: the single-source-of-truth split. In a SPLIT taskbar
        // process these are shell-core CLIENTS (one UDS connection to the core, which owns the helper
        // stream + the /Applications watchers). In the Core role, the all-in-one process, and the
        // (lazy, unused) explorer/desktop roles they are the DIRECT macOS implementations.
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

            // Host the helper EAGERLY only where window management actually runs: the headless core
            // and the all-in-one process. Explorer/Desktop keep the singleton lazy, never started.
            if (role is ShellRole.Core or ShellRole.All)
                services.AddHostedService(sp => sp.GetRequiredService<Pal.MacOS.HelperLifecycle>());
        }

        return services;
    }

    /// <summary>Lets each feature module self-register its services (DI-01).</summary>
    public static IServiceCollection AddBevelModules(this IServiceCollection services, ShellRole role = ShellRole.All)
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
        // surface (Apple Events, bevelctl, bevel://) funnels through. The window-coupled verbs reach
        // the live file manager via FileManagerShellSurface; the filesystem verbs run on the VFS.
        services.AddSingleton<Bevel.Interop.IKnownFolders>(Bevel.Interop.SystemKnownFolders.Instance);
        services.AddSingleton<Bevel.Interop.IShellSurface, FileManagerShellSurface>();
        services.AddSingleton<Bevel.Interop.IShellAutomation, Bevel.Interop.ShellAutomation>();
        // The bevelctl + bevel:// execution core (M4-D): both surfaces parse into a ParsedCommand and
        // run it through this router → the one IShellAutomation seam. The transports that feed it (the
        // bevelctl socket, the macOS bevel:// URL-event handler) resolve this singleton.
        services.AddSingleton<Bevel.Interop.Cli.AutomationCommandRouter>();

        // Serve the bevelctl socket only from the process that owns the live file manager (All /
        // Explorer) — the one where IShellAutomation's window verbs actually work.
        if (role is ShellRole.All or ShellRole.Explorer)
            services.AddHostedService<AutomationSocketHost>();

        return services;
    }
}
