using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.Taskbar;

/// <summary>Self-registers the taskbar module's services (DI-01). Placeholder at M0.</summary>
public sealed class TaskbarModule : IModule
{
    public string Name => "Taskbar";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<TaskbarView>();
    }
}
