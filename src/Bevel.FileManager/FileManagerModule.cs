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
            // Volume labels come from the active PAL when it provides them (bevel-1cc);
            // GetService (not Required) because the Fake PAL registers none.
            root.Register(new ComputerProvider(sp.GetService<Bevel.Pal.Abstractions.IVolumeLabelSource>()));
            // Makes the Trash tree node browsable (bevel-sw3k) — before this, selecting it threw
            // KeyNotFoundException (swallowed) because no "trash" provider was registered.
            root.Register(new TrashProvider());
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