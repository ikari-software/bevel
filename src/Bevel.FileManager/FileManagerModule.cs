using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.FileManager;

/// <summary>Self-registers the file manager module's services.</summary>
public sealed class FileManagerModule : IModule
{
    public string Name => "FileManager";

    public void ConfigureServices(IServiceCollection services)
    {
        // VFS root + providers
        services.AddSingleton<VfsRoot>(sp =>
        {
            var root = new VfsRoot();
            root.Register(new LocalFsProvider());
            root.Register(new ComputerProvider());
            return root;
        });

        // Settings
        services.AddSingleton<SettingsService>();
        services.AddTransient<SettingsWindow>();

        // File operations engine + its conflict handler (safe default until the M1
        // "Confirm File Replace" dialog). Singleton so the undo stack persists.
        services.AddSingleton<IConflictHandler, DefaultConflictHandler>();
        services.AddSingleton<FileOperationService>();

        // Command seam between the UI and the file-ops engine (bevel-o2t). Transient: one per
        // window, owning that window's navigation history, selection, and clipboard.
        services.AddTransient<FileManagerController>();

        // File manager windows
        services.AddTransient<FileManagerView>();
        services.AddTransient<FileManagerWindow>();
    }
}