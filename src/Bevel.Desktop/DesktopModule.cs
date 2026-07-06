using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.Desktop;

/// <summary>Self-registers the desktop surface module.</summary>
public sealed class DesktopModule : IModule
{
    public string Name => "Desktop";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<DesktopView>();
        services.AddTransient<DesktopWindow>();
    }
}