using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Modules;

public interface IModule
{
    /// <summary>
    /// The display name of the module (e.g., "Catalog", "Sales").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Names of other modules this module depends on at runtime.
    /// </summary>
    IReadOnlyList<string> Dependencies => [];

    /// <summary>
    /// When true, the module will not start if any of its declared dependencies are disabled.
    /// When false (default), disabled dependencies are tolerated and stub services are used.
    /// </summary>
    bool RequiresDependencies => false;

    /// <summary>
    /// Registers module services, configuration, and infrastructure into the DI container.
    /// </summary>
    void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings);

    /// <summary>
    /// Registers stub services when this module is disabled.
    /// Other modules that depend on this module's cross-module services will receive these stubs.
    /// </summary>
    void RegisterDisabledStubs(IServiceCollection services) { }
}
