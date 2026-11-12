using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.ServiceHost.Tests.Support;

/// <summary>
/// The one module the composed host discovers in these tests. It lives in the test assembly, which
/// is the only entry of <c>ModuleAssemblies</c>, and is enabled by
/// <c>Modules:ServiceHostTest:Enabled=true</c> in the seeded configuration. It registers a marker
/// singleton and nothing else (no command or query handler), so module discovery is observable
/// without giving the decorator pipeline anything of its own to wrap.
/// </summary>
public sealed class ServiceHostTestModule : IModule
{
    /// <summary>The module name, also the key of its <c>Modules</c> configuration entry.</summary>
    public const string ModuleName = "ServiceHostTest";

    /// <inheritdoc />
    public string Name => ModuleName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        services.AddSingleton<ServiceHostTestModuleMarker>();
}
