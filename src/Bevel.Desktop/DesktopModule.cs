using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.Desktop;

/// <summary>Self-registers the desktop module's services (DI-01). Placeholder at M0.</summary>
public sealed class DesktopModule : IModule
{
    public string Name => "Desktop";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<DesktopView>();
    }
}
