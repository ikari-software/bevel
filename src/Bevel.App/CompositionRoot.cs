using Bevel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// opener + writer of settings.db; Taskbar/Filer/Desktop bind <c>RemoteSettingsService</c>, a peer that
/// reads the core's pushed snapshot and sends changed-keys merge patches to the core over a shell-core
/// client. Taskbar reuses its existing client; Filer/Desktop add a settings-only client (their
/// window/app/tray stay direct-PAL). The Core builds NO client — it is the server.</para>
/// </summary>
public static class CompositionRoot
{
    /// <summary>Update checking, CORE ROLE ONLY (bevel-rkxb). Core already owns settings.db and serves
    /// peers; if each peer polled its own feed a five-process shell would make five times the requests and
    /// five processes would each have to decide what to do with the answer.
    ///
    /// The source is <see cref="Bevel.Core.Updates.NoUpdateSource"/> until Velopack is wired (bevel-ym0,
    /// UPD-01). Registered explicitly rather than left unbound so "no update source on this build" is a
    /// stated fact in the container, not a resolve-time failure — the same reason NullAppBadgeSource and
    /// NullThumbnailProvider exist.</summary>
    private static void AddUpdateChecking(IServiceCollection services)
    {
        services.AddSingleton<Bevel.Core.Updates.IUpdateSource>(_ => Bevel.Core.Updates.NoUpdateSource.Instance);
        services.AddSingleton(_ => new Bevel.Core.Updates.UpdateCheckState(BevelConfigDir.Path));
        // Registered by TYPE rather than through a factory so the descriptor carries the implementation
        // name: RoleSelectorTests tells the helper apart from other hosted services that way, and a
        // factory registration is opaque to it.
        services.AddHostedService<Updates.UpdateCheckService>();
    }

    /// <summary>Settings for a PEER role — Taskbar / Filer / Desktop (core-owns-settings, bevel-6nve):
    /// reads and writes flow through the shell core so the peer never opens settings.db. Three pieces:
    /// <list type="bullet">
    ///   <item>Its OWN shell-core client, keyed <c>"settings"</c>, NOT the shared window/app/tray client the
    ///     taskbar also has: <c>RemoteSettingsService</c> connects during startup <c>LoadAsync</c>, and sharing
    ///     the tray client would make that early connect consume the core's on-connect TraySnapshot before
    ///     the tray adapter subscribes — leaving the split taskbar's tray empty (the tray has no reconcile
    ///     backstop to re-derive it). A separate client keeps each connection's on-connect snapshot flowing
    ///     to its own subscriber. Filer / Desktop have no other client at all (their window/app/tray stay
    ///     direct-PAL), so for them this IS the only link to the core.</item>
    ///   <item>The on-disk snapshot cache (bevel-7s9n) the peer paints its FIRST frame from, with zero IPC,
    ///     and writes every applied snapshot through to. Rooted at the real config dir: this is the one
    ///     place a peer touches <c>~/.config/bevel</c>, and it is a cache of the core's state, not a store.</item>
    ///   <item>The remote service itself, which pulls the keyed client via <c>[FromKeyedServices("settings")]</c>.</item>
    /// </list>
    /// DI owns the client's disposal.</summary>
    private static void AddSettingsPeer(IServiceCollection services)
    {
        // ONE client per process (bevel-4zfs). The old DI-keyed second "settings" connection existed only
        // because the core pushed its snapshot burst on CONNECT — so the settings peer's early LoadAsync
        // connect would eat the tray snapshot before the tray adapter subscribed. The session protocol
        // deleted that race: nothing is pushed on connect, so the peer shares the SAME client as the
        // window/app/tray adapters (the taskbar branch registers it first; TryAdd keeps that one).
        services.TryAddSingleton(_ => ShellCore.ShellCoreEndpoint.CreateClient());
        services.AddSingleton(_ => new SettingsSnapshotCache(BevelConfigDir.Path));
        // Both ctor params are the registered singletons: the shared client + the snapshot cache.
        services.AddSingleton<ISettingsService, ShellCore.RemoteSettingsService>();
    }

    // Shared icon-pool geometry (bevel-gww.6). Co-located with the shell-core runtime dir so a
    // supervisor cleaning that dir clears the pool too. 512 slots × up to 96×96 BGRA (covers 48pt@2x
    // Retina) ≈ 19 MB, sparse: only pages for actually-published icons ever become resident.
    private static string IconPoolPath => Path.Combine(BevelRuntimeDir.CoreDir, "icons.pool");
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
            PalKind.Windows => services.AddWindowsPal(role),
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
        services.AddSingleton<IThumbnailProvider, Pal.Fake.FakeThumbnailProvider>();
        services.AddSingleton<IAppEnvironment, Pal.Fake.FakeAppEnvironment>();
        services.AddSingleton<IFileOpener, Pal.Fake.FakeFileOpener>();
        services.AddSingleton<IPermissionBroker, Pal.Fake.FakePermissionBroker>();
        services.AddSingleton<IAudioPlayback, Pal.Fake.FakeAudioPlayback>();
        services.AddSingleton<IDockController, Pal.Fake.FakeDockController>();
        services.AddSingleton<IShellConnectionStatus, AlwaysConnectedShellStatus>();
        services.AddSingleton<ITabProvider, Pal.Fake.FakeTabProvider>();
        // Unread badges (bevel-ijln): starts empty — the scripted fake desktop invents no counts.
        services.AddSingleton<Pal.Fake.FakeAppBadgeSource>();
        services.AddSingleton<IAppBadgeSource>(sp => sp.GetRequiredService<Pal.Fake.FakeAppBadgeSource>());
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

        // Content previews (bevel-9elh) stay OUT of the shared icon pool: a thumbnail is per-file
        // content, not a reusable type icon, and the taskbar is the only consumer today. Purely local
        // (ImageIO / CGPDFDocument read the file in-process), so every role gets it directly.
        services.AddSingleton<IThumbnailProvider, Pal.MacOS.MacOSThumbnailProvider>();

        services.AddSingleton<IPermissionBroker, Pal.MacOS.MacOSPermissionBroker>();
        // Opening a document is a purely local `open`(1) spawn — no shell-core proxy, every role direct.
        services.AddSingleton<IFileOpener, Pal.MacOS.MacOSFileOpener>();
        services.AddSingleton<IAudioPlayback, Pal.MacOS.MacOSAudioPlayback>();
        services.AddSingleton<IDockController, Pal.MacOS.MacOSDockController>();
        services.AddSingleton<IVolumeLabelSource, Pal.MacOS.MacOSVolumeLabelSource>();
        // Tabs are enumerated by talking Apple Events to the target app — purely local (an osascript
        // child), so like IFileOpener every role gets the direct implementation, no core proxy.
        services.AddSingleton<ITabProvider, Pal.MacOS.MacOSTabProvider>();
        // Unread badges (bevel-ijln): read from the Dock's accessibility tree in-process — no helper
        // hop, so every role gets it direct (the taskbar process is the one that needs it).
        services.AddSingleton<IAppBadgeSource, Pal.MacOS.MacOSAppBadgeSource>();

        // Window management + app environment: the single-source-of-truth split. In the taskbar
        // process these are shell-core CLIENTS (one UDS connection to the core, which owns the helper
        // stream + the /Applications watchers). In the Core role and the (lazy, unused) filer/desktop
        // roles they are the DIRECT macOS implementations.
        if (role is ShellRole.Taskbar)
        {
            services.AddSingleton(_ => ShellCore.ShellCoreEndpoint.CreateClient());
            services.AddSingleton<IWindowManager, ShellCore.ShellCoreWindowManager>();
            services.AddSingleton<IAppEnvironment, ShellCore.ShellCoreAppEnvironment>();
            // The one core connection IS the link-health source the taskbar's tray indicator tracks.
            services.AddSingleton<IShellConnectionStatus>(sp => sp.GetRequiredService<ShellCore.ShellCoreClient>());
            // The split taskbar mirrors the tray via the shell core (bevel-m3.1.1): the core owns the
            // real TrayService stream and pushes items here, exactly like windows. The NATIVE hide applies
            // IN this taskbar process (bevel-qpir): the macOS control NSStatusItem needs a serviced AppKit
            // run loop, which the headless core lacks — that is what wedged the helper's copy. Routed through
            // the host's local seam so TrayViewModel needs no platform branch.
            services.AddSingleton<ISystemTrayHost>(sp => new ShellCore.ShellCoreSystemTrayHost(
                sp.GetRequiredService<ShellCore.ShellCoreClient>(),
                applyLocalHide: hidden => Pal.MacOS.MacMenuBarControl.SetHidden(hidden)));
            // Settings peer: its own keyed "settings" client (never the shared tray client above — see
            // AddSettingsPeer for why) + the first-paint snapshot cache + the remote service.
            AddSettingsPeer(services);
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
                AddUpdateChecking(services);
                // Host the helper EAGERLY only where window management actually runs: the headless core.
                // Filer/Desktop keep the singleton lazy, never started.
                services.AddHostedService(sp => sp.GetRequiredService<Pal.MacOS.HelperLifecycle>());
            }
            else
            {
                // Filer / Desktop are settings PEERS but keep window/app/tray DIRECT-PAL (in-process):
                // a settings-ONLY shell-core client so ONLY the core opens the DB; their window manager /
                // app environment / tray above are unaffected (core-owns-settings, bevel-6nve).
                AddSettingsPeer(services);
            }
        }

        return services;
    }

    /// <summary>
    /// Windows PAL wiring (bevel-ncfp, the Windows port). Mirrors <see cref="AddMacOSPal"/>'s role split
    /// exactly — same shared icon pool (single-writer = Core), same shell-core CLIENT wiring for the
    /// Taskbar/peer roles, same core-owns-settings gating — but with the <c>Windows*</c> PAL impls and
    /// NO Swift helper: on Windows, window discovery is in-process (the Core owns an EnumWindows +
    /// SetWinEventHook pump thread, U3), so there is no helper lifecycle to host. U1 ships stubs.
    /// </summary>
    private static IServiceCollection AddWindowsPal(this IServiceCollection services, ShellRole role)
    {
        // In-process Windows PAL services — every role resolves these directly; lazy, so a role that
        // never resolves one never constructs it.
        services.AddSingleton<Pal.Windows.WindowsDesktopEnvironment>();
        services.AddSingleton<IDesktopEnvironment>(sp => sp.GetRequiredService<Pal.Windows.WindowsDesktopEnvironment>());
        services.AddSingleton<IShellSession, Pal.Windows.WindowsShellSession>();
        services.AddSingleton<IFileOperations, Pal.Windows.WindowsFileOperations>();

        // Shared memory-mapped BGRA icon pool (bevel-gww.6), identical to macOS: single-writer = the
        // shell-core owner (Core); UI roles are readers (miss -> private render). The isWriter flag also
        // gates whether the pool file is created/resized (reader opens read-only).
        var poolIsWriter = role is ShellRole.Core;
        services.AddSingleton(_ => MmfBgraPool.CreateOrOpen(
            IconPoolPath, IconPoolSlotCapacity, IconPoolMaxBgraBytes, isWriter: poolIsWriter));
        services.AddSingleton<IIconProvider>(sp => new PooledIconProvider(
            new Pal.Windows.WindowsIconProvider(),
            sp.GetRequiredService<MmfBgraPool>(),
            isWriter: poolIsWriter));

        services.AddSingleton<IPermissionBroker, Pal.Windows.WindowsPermissionBroker>();
        services.AddSingleton<IFileOpener, Pal.Windows.WindowsFileOpener>();
        services.AddSingleton<IAudioPlayback, Pal.Windows.WindowsAudioPlayback>();
        services.AddSingleton<IDockController>(sp => new Pal.Windows.WindowsDockController(
            sp.GetRequiredService<Pal.Windows.WindowsDesktopEnvironment>()));
        services.AddSingleton<IVolumeLabelSource, Pal.Windows.WindowsVolumeLabelSource>();
        services.AddSingleton<ITabProvider, Pal.Windows.WindowsTabProvider>();
        // No badge source on Windows yet (bevel-ijln): the shell-integration taskbar overlay-icon API is
        // per-owning-process like NSDockTile, so there is nothing to read cross-app. Empty, never invented.
        services.AddSingleton<IAppBadgeSource>(_ => NullAppBadgeSource.Instance);
        // No thumbnail engine on Windows yet (bevel-9elh): the macOS provider is ImageIO/CGPDFDocument.
        // Registered explicitly rather than left unbound, so stack cells fall back to type icons as a
        // stated platform fact instead of depending on an optional ctor param resolving to null.
        services.AddSingleton<IThumbnailProvider>(_ => NullThumbnailProvider.Instance);

        // Window management + app environment: the same single-source-of-truth split as macOS. In the
        // taskbar process these are shell-core CLIENTS (one UDS connection to the core); in the Core and
        // (lazy) filer/desktop roles they are the DIRECT Windows implementations.
        if (role is ShellRole.Taskbar)
        {
            services.AddSingleton(_ => ShellCore.ShellCoreEndpoint.CreateClient());
            services.AddSingleton<IWindowManager, ShellCore.ShellCoreWindowManager>();
            services.AddSingleton<IAppEnvironment, ShellCore.ShellCoreAppEnvironment>();
            services.AddSingleton<IShellConnectionStatus>(sp => sp.GetRequiredService<ShellCore.ShellCoreClient>());
            // Same local-hide seam as macOS (bevel-qpir): the Windows TrayNotifyWnd toggle is in-proc Win32,
            // so the split taskbar applies it directly — the headless core must not be the one to.
            services.AddSingleton<Pal.Windows.WindowsSystemTrayHost>();
            services.AddSingleton<ISystemTrayHost>(sp => new ShellCore.ShellCoreSystemTrayHost(
                sp.GetRequiredService<ShellCore.ShellCoreClient>(),
                applyLocalHide: hidden => { _ = sp.GetRequiredService<Pal.Windows.WindowsSystemTrayHost>()
                    .SetNativeTrayHiddenAsync(hidden); }));
            // Settings peer + dedicated keyed "settings" client + first-paint cache (bevel-6nve / 7s9n),
            // same rationale as macOS.
            AddSettingsPeer(services);
        }
        else
        {
            services.AddSingleton<IWindowManager, Pal.Windows.WindowsWindowManager>();
            services.AddSingleton<IAppEnvironment, Pal.Windows.WindowsAppEnvironment>();
            services.AddSingleton<ISystemTrayHost, Pal.Windows.WindowsSystemTrayHost>();
            // In-process window management: no link to lose, so the indicator stays hidden.
            services.AddSingleton<IShellConnectionStatus, AlwaysConnectedShellStatus>();

            if (role is ShellRole.Core)
            {
                // The shell core is the SOLE opener + writer of settings.db (bevel-6nve). No helper to
                // host — Windows discovery is in-process (U3), unlike the macOS Swift helper.
                services.AddSingleton<ISettingsService, Bevel.Core.SettingsService>();
                AddUpdateChecking(services);
            }
            else
            {
                // Filer / Desktop are settings PEERS but keep window/app/tray DIRECT-PAL.
                AddSettingsPeer(services);
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
        // The window seam is ROLE-AWARE (bevel-e7a7). The Filer process owns a live in-process
        // FileManagerWindow graph, so it uses FileManagerShellSurface (addresses windows by registry id).
        // Every other role — crucially the persistent TASKBAR that now hosts the socket — has no such
        // graph, so it uses SpawningShellSurface, whose open/reveal verbs spawn a --role=filer process
        // instead of building a malformed window in-process. This is what lets bevelctl/bevel:// open &
        // reveal with NO Filer already running.
        if (role is ShellRole.Filer)
            services.AddSingleton<Bevel.Interop.IShellSurface, FileManagerShellSurface>();
        else
            // The persistent host's surface (bevel-e7a7): open/reveal spawn a Filer; the
            // window-coupled verbs are forwarded to the live Filers over the taskbar↔Filer channel
            // (bevel-uldj) when its client is registered (Taskbar role, below). SpawningShellSurface's
            // channel-client ctor parameter is optional (defaults to null), so the Desktop/Core roles
            // that also bind this surface but never wire the channel keep the clear "no window" fallback.
            services.AddSingleton<Bevel.Interop.IShellSurface, SpawningShellSurface>();
        // Supervised opens go through the launcher's SpawnFiler verb (bevel-t48y); the registration
        // self-degrades to the in-process spawner when unsupervised, so one registration covers both.
        services.AddSingleton<IFilerSpawner>(sp => new LauncherFilerSpawner());

        // Taskbar↔Filer automation channel (bevel-uldj). The Filer process REGISTERS by hosting a
        // control server that answers forwarded select/query against its own in-process
        // FileManagerShellSurface; the Taskbar process resolves the client that discovers + dials those
        // Filers. Both reuse the shell-core UDS transport + HMAC-nonce auth (FilerControlEndpoint).
        if (role is ShellRole.Filer)
        {
            services.AddHostedService<FilerControlServer>();
            // Parked pre-warm (bevel-t48y): the --park Filer's hidden window waits on this host for
            // the launcher's Show dial. Non-Filer roles never register it (the server isn't hosted).
            // The concrete class is registered too — App.CreateFilerSurfaceCore hands the built window
            // to the concrete host, while FilerControlServer reads it through the interface.
            services.AddSingleton<ParkedFilerWindowHost>();
            services.AddSingleton<IParkedFilerWindowHost>(sp => sp.GetRequiredService<ParkedFilerWindowHost>());
        }
        else if (role is ShellRole.Taskbar)
            services.AddSingleton(_ => new TaskbarFilerControlClient(
                ShellCore.FilerControlEndpoint.Dir,
                ShellCore.FilerControlEndpoint.ResolveNonce()));
        services.AddSingleton<Bevel.Interop.IProgramSurface, AppEnvironmentProgramSurface>();
        services.AddSingleton<Bevel.Interop.IShellAutomation, Bevel.Interop.ShellAutomation>();
        // The bevelctl + bevel:// execution core (M4-D): both surfaces parse into a ParsedCommand and
        // run it through this router → the one IShellAutomation seam. The transports that feed it (the
        // bevelctl socket, the macOS bevel:// URL-event handler) resolve this singleton.
        services.AddSingleton<Bevel.Interop.Cli.AutomationCommandRouter>();

        // Serve the bevelctl socket from the PERSISTENT host — the always-up TASKBAR (bevel-e7a7), NOT
        // the on-demand Filer that only exists once a window is open. The taskbar has the full
        // automation DI graph; its filesystem/program verbs run directly, and its window verbs spawn an
        // Filer via SpawningShellSurface. Exactly one process binds the fixed socket path, so there is
        // never a second server on it. A bind failure is logged, never fatal.
        if (role is ShellRole.Taskbar)
            services.AddHostedService<AutomationSocketHost>();

        return services;
    }
}
