using Avalonia.Headless.XUnit;
using Bevel.App;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Verifies the DI wiring the App relies on (bevel-hun) and that the macOS platform
/// registers a hosted service that the started host will run (bevel-c0y).
/// </summary>
public class CompositionWiringTests
{
    private static ServiceProvider BuildModuleServices()
    {
        var services = new ServiceCollection();
        new FileManagerModule().ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void VfsRoot_is_registered_with_both_file_and_computer_providers()
    {
        // bevel-hun: the DI-registered VfsRoot (which the App now resolves) must include
        // ComputerProvider so "My Computer" works — the hand-built one only had file.
        using var sp = BuildModuleServices();
        var vfs = sp.GetRequiredService<VfsRoot>();

        Assert.Contains("file", vfs.Schemes);
        Assert.Contains("computer", vfs.Schemes);
    }

    [Fact]
    public void FileOperationService_and_SettingsService_resolve_from_di()
    {
        // bevel-hun: these must be resolvable so the App can inject them into the window
        // (Undo and Folder Options depend on them).
        using var sp = BuildModuleServices();

        Assert.NotNull(sp.GetRequiredService<FileOperationService>());
        Assert.NotNull(sp.GetRequiredService<SettingsService>());
        Assert.NotNull(sp.GetRequiredService<IConflictHandler>());
    }

    [AvaloniaFact]
    public void File_manager_window_resolves_and_wires_exactly_as_the_app_does()
    {
        using var sp = BuildModuleServices();

        var fm = sp.GetRequiredService<FileManagerWindow>();

        // The same three calls App.OnFrameworkInitializationCompleted makes — must not throw.
        var ex = Record.Exception(() =>
        {
            fm.SetVfsRoot(sp.GetRequiredService<VfsRoot>());
            fm.SetSettingsService(sp.GetRequiredService<SettingsService>());
            fm.SetFileOperationService(sp.GetRequiredService<FileOperationService>());
        });
        Assert.Null(ex);
    }

    [Fact]
    public void MacOS_platform_registers_a_hosted_service()
    {
        // bevel-c0y: the macOS PAL adds HelperLifecycle as an IHostedService. Program.Main
        // now starts the host, so that hosted service actually runs (previously dead).
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }
}
