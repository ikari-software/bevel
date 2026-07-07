using Bevel.Core;
using Bevel.Desktop;
using Bevel.FileManager;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bevel.App;

/// <summary>
/// DI composition helpers. This is the only place concrete PALs are named (ARCH-03/DI-02).
/// </summary>
public static class CompositionRoot
{
    /// <summary>Registers the selected concrete PAL's implementation of every abstraction.</summary>
    public static IServiceCollection AddBevelPlatform(this IServiceCollection services, PalKind pal)
    {
        return pal switch
        {
            PalKind.MacOS => services.AddMacOSPal(),
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
        services.AddSingleton<IPermissionBroker, Pal.Fake.FakePermissionBroker>();
        services.AddSingleton<IAudioPlayback, Pal.Fake.FakeAudioPlayback>();
        return services;
    }

    private static IServiceCollection AddMacOSPal(this IServiceCollection services)
    {
        services.AddSingleton<IWindowManager, Pal.MacOS.MacOSWindowManager>();
        services.AddSingleton<ISystemTrayHost, Pal.MacOS.MacOSSystemTrayHost>();
        services.AddSingleton<IDesktopEnvironment, Pal.MacOS.MacOSDesktopEnvironment>();
        services.AddSingleton<IShellSession, Pal.MacOS.MacOSShellSession>();
        services.AddSingleton<IFileOperations, Pal.MacOS.MacOSFileOperations>();
        services.AddSingleton<IIconProvider, Pal.MacOS.MacOSIconProvider>();
        services.AddSingleton<IAppEnvironment, Pal.MacOS.MacOSAppEnvironment>();
        services.AddSingleton<IPermissionBroker, Pal.MacOS.MacOSPermissionBroker>();
        services.AddSingleton<IAudioPlayback, Pal.MacOS.MacOSAudioPlayback>();
        services.AddSingleton<Pal.MacOS.HelperLifecycle>();
        services.AddHostedService(sp => sp.GetRequiredService<Pal.MacOS.HelperLifecycle>());
        return services;
    }

    /// <summary>Lets each feature module self-register its services (DI-01).</summary>
    public static IServiceCollection AddBevelModules(this IServiceCollection services)
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
        services.AddSingleton<FileManagerWindowFactory>();

        return services;
    }
}
