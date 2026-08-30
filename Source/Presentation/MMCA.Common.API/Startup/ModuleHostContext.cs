using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// The result of <c>AddModuleHost</c>: the settings a service host binds once at startup plus the
/// <see cref="ModuleLoader"/> it discovers its modules with.
/// <para>
/// Module discovery itself is NOT run here. It has to happen inside the host's ADR-014 application
/// pipeline, between <c>AddApplication()</c> and <c>AddApplicationDecorators()</c>, and its position
/// relative to the host's other pipeline steps (gRPC client replacements, broker messaging) is a
/// per-host decision. <see cref="RegisterModules"/> is therefore handed to the pipeline as a step,
/// and <c>AddModuleHealthChecks(ModuleLoader)</c> is called by the host afterwards, since it can
/// only enumerate modules that discovery has already found.
/// </para>
/// </summary>
public sealed class ModuleHostContext
{
    private readonly IConfigurationManager _configuration;
    private readonly string? _environmentName;
    private readonly IEnumerable<Assembly> _moduleAssemblies;

    internal ModuleHostContext(
        IConfigurationManager configuration,
        string? environmentName,
        ApplicationSettings applicationSettings,
        ModulesSettings modulesSettings,
        ModuleLoader moduleLoader,
        IEnumerable<Assembly> moduleAssemblies)
    {
        _configuration = configuration;
        _environmentName = environmentName;
        _moduleAssemblies = moduleAssemblies;
        ApplicationSettings = applicationSettings;
        ModulesSettings = modulesSettings;
        ModuleLoader = moduleLoader;
    }

    /// <summary>Gets the bound application settings the host was configured with.</summary>
    public ApplicationSettings ApplicationSettings { get; }

    /// <summary>Gets the bound per-module enabled/disabled configuration.</summary>
    public ModulesSettings ModulesSettings { get; }

    /// <summary>
    /// Gets the loader registered as a singleton by <c>AddModuleHost</c>. Pass it to
    /// <c>AddModuleHealthChecks</c> after the application pipeline has run
    /// <see cref="RegisterModules"/>, and to <c>InitializeDatabaseAsync</c>.
    /// </summary>
    public ModuleLoader ModuleLoader { get; }

    /// <summary>
    /// Runs <see cref="ModuleLoader.DiscoverAndRegister(IServiceCollection, IConfigurationBuilder, ApplicationSettings, ModulesSettings, string?, IEnumerable{Assembly})"/>
    /// with the configuration, settings, environment name and module assemblies captured at
    /// <c>AddModuleHost</c> time.
    /// Register it as a step of the host's application pipeline
    /// (<c>pipeline.Register(moduleHost.RegisterModules)</c>) so every module handler lands in the
    /// container before <c>AddApplicationDecorators()</c> closes the pipeline.
    /// </summary>
    /// <param name="services">The service collection module registrations are added to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public void RegisterModules(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ModuleLoader.DiscoverAndRegister(
            services,
            _configuration,
            ApplicationSettings,
            ModulesSettings,
            _environmentName,
            _moduleAssemblies);
    }
}
