using Bevel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.Taskbar;

/// <summary>Self-registers the taskbar module's services (DI-01).</summary>
public sealed class TaskbarModule : IModule
{
    public string Name => "Taskbar";

    public void ConfigureServices(IServiceCollection services)
    {
        // Background shell-model service + its view-model projections (bevel-d2z). Singletons:
        // one live model of windows/programs, shared by the taskbar strip and the Start menu.
        services.AddSingleton<ShellModel>();
        services.AddSingleton<StartMenuViewModel>();
        services.AddSingleton<TaskbarViewModel>();

        services.AddTransient<TaskbarView>();
        services.AddTransient<TaskbarWindow>();
        services.AddTransient<OnboardingWindow>();
    }
}