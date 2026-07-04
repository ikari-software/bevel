using Microsoft.Extensions.DependencyInjection;

namespace Bevel.Core;

/// <summary>
/// A feature module self-registers its services into the composition root (DI-01).
/// Feature modules implement this; <c>Bevel.App</c> just lists them.
/// </summary>
public interface IModule
{
    string Name { get; }

    void ConfigureServices(IServiceCollection services);
}
