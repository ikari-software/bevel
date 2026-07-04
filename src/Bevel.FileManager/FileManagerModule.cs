using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.FileManager;

/// <summary>Self-registers the file manager module's services (DI-01). Placeholder at M0.</summary>
public sealed class FileManagerModule : IModule
{
    public string Name => "FileManager";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<FileManagerView>();
    }
}
