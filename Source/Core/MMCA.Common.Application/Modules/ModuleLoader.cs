using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Modules;

/// <summary>
/// Discovers <see cref="IModule"/> implementations via reflection and registers them in
/// dependency order. Disabled modules receive stub service registrations so that
/// cross-module interfaces (e.g. <c>IProductVariantService</c>) remain resolvable.
/// </summary>
public sealed partial class ModuleLoader
{
    private readonly List<IModule> _enabledModules = [];
    private readonly List<IModuleSeeder> _seeders = [];
    private readonly List<string> _disabledModuleNames = [];

    /// <summary>Gets the modules that were successfully registered.</summary>
    public IReadOnlyList<IModule> EnabledModules => _enabledModules;

    /// <summary>Gets the names of modules that were skipped because they are disabled in configuration.</summary>
    public IReadOnlyList<string> DisabledModuleNames => _disabledModuleNames;

    /// <summary>
    /// Optional logger for structured module loading diagnostics.
    /// Defaults to <see cref="NullLogger{T}"/> when not set.
    /// </summary>
    public ILogger<ModuleLoader> Logger { get; init; } = NullLogger<ModuleLoader>.Instance;

    /// <summary>
    /// Scans all loaded assemblies for <see cref="IModule"/> implementations, sorts them
    /// in dependency order via topological sort, and registers each enabled module into
    /// the DI container. Disabled modules receive stub registrations instead.
    /// </summary>
    /// <param name="services">The service collection to register module services into.</param>
    /// <param name="configurationBuilder">Allows modules to add their own configuration sources.</param>
    /// <param name="applicationSettings">Global application settings shared across modules.</param>
    /// <param name="modulesSettings">Per-module enabled/disabled configuration.</param>
    /// <param name="environmentName">
    /// Optional hosting environment name (e.g. "Development"). When provided, the loader
    /// also loads <c>modules.{name}.{environmentName}.json</c> for each module.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a module with <see cref="IModule.RequiresDependencies"/> set to <see langword="true"/>
    /// has disabled dependencies.
    /// </exception>
    public void DiscoverAndRegister(
        IServiceCollection services,
        IConfigurationBuilder configurationBuilder,
        ApplicationSettings applicationSettings,
        ModulesSettings modulesSettings,
        string? environmentName = null)
    {
        // Scan all loaded assemblies for concrete IModule implementations.
        // The try-catch guards against assemblies that throw on GetTypes()
        // (e.g. ReflectionTypeLoadException from missing transitive references).
        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (Exception ex)
                {
                    LogAssemblyScanFailed(Logger, a.FullName ?? a.GetName().Name ?? "unknown", ex.Message);
                    return [];
                }
            })
            .ToList();

        var allModules = allTypes
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .ToList();

        var allSeeders = allTypes
            .Where(t => typeof(IModuleSeeder).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IModuleSeeder)Activator.CreateInstance(t)!)
            .ToDictionary(s => s.ModuleName, s => s, StringComparer.OrdinalIgnoreCase);

        // Topological sort ensures each module's dependencies are registered before the module itself
        var sorted = TopologicalSort(allModules);

        foreach (var module in sorted)
        {
            if (!modulesSettings.IsModuleEnabled(module.Name))
            {
                LogModuleDisabled(Logger, module.Name);
                module.RegisterDisabledStubs(services);
                _disabledModuleNames.Add(module.Name);
                continue;
            }

            ValidateModuleDependencies(module, modulesSettings);
            RegisterEnabledModule(module, services, configurationBuilder, applicationSettings, environmentName);

            if (allSeeders.TryGetValue(module.Name, out var seeder))
            {
                _seeders.Add(seeder);
            }
        }
    }

    private void ValidateModuleDependencies(IModule module, ModulesSettings modulesSettings)
    {
        var disabledDeps = module.Dependencies
            .Where(d => !modulesSettings.IsModuleEnabled(d))
            .ToList();

        if (disabledDeps.Count > 0 && module.RequiresDependencies)
        {
            throw new InvalidOperationException(
                $"Module '{module.Name}' requires [{string.Join(", ", disabledDeps)}] " +
                "which are disabled. Either enable the required modules or disable this module.");
        }

        foreach (var dependency in disabledDeps)
        {
            LogDependencyDisabledWarning(Logger, module.Name, dependency);
        }
    }

    private void RegisterEnabledModule(
        IModule module,
        IServiceCollection services,
        IConfigurationBuilder configurationBuilder,
        ApplicationSettings applicationSettings,
        string? environmentName)
    {
        LogModuleRegistering(Logger, module.Name, module.Dependencies.Count);

        // Centralized config: load module-specific JSON files by naming convention
        // before calling Register(), so configuration is available during DI registration.
#pragma warning disable CA1308 // Module config files use lowercase naming convention (e.g., modules.catalog.json)
        var moduleConfigName = module.Name.ToLowerInvariant();
#pragma warning restore CA1308
        configurationBuilder.AddJsonFile($"modules.{moduleConfigName}.json", optional: true, reloadOnChange: true);
        if (!string.IsNullOrEmpty(environmentName))
        {
            configurationBuilder.AddJsonFile($"modules.{moduleConfigName}.{environmentName}.json", optional: true, reloadOnChange: true);
        }

        var sw = Stopwatch.StartNew();
        module.Register(services, configurationBuilder, applicationSettings);
        sw.Stop();
        LogModuleRegistered(Logger, module.Name, sw.ElapsedMilliseconds);
        _enabledModules.Add(module);
    }

    /// <summary>
    /// Invokes <see cref="IModuleSeeder.SeedAsync"/> on each discovered seeder
    /// whose module is enabled, in registration order.
    /// </summary>
    /// <param name="serviceProvider">The root service provider for resolving seeder dependencies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all module seeders have finished.</returns>
    public async Task SeedAllAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        foreach (var seeder in _seeders)
        {
            await seeder.SeedAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Topological sort using Kahn's algorithm (BFS-based).
    /// Produces an ordering where each module appears after all of its declared dependencies,
    /// guaranteeing that DI registrations are available when dependents register.
    /// </summary>
    /// <param name="modules">All discovered module instances.</param>
    /// <returns>Modules sorted so that dependencies precede dependents.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a circular dependency is detected.</exception>
    private static List<IModule> TopologicalSort(List<IModule> modules)
    {
        var modulesByName = modules.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

        // inDegree[X] = number of unprocessed dependencies that X still requires
        var inDegree = modules.ToDictionary(m => m.Name, _ => 0, StringComparer.OrdinalIgnoreCase);

        // dependents[X] = list of modules that depend on X (reverse adjacency list)
        var dependents = modules.ToDictionary(m => m.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        // Build the dependency graph
        foreach (var m in modules)
        {
            foreach (var dep in m.Dependencies)
            {
                if (!modulesByName.ContainsKey(dep))
                    continue; // dependency not discovered — validation happens during registration

                dependents[dep].Add(m.Name);
                inDegree[m.Name]++;
            }
        }

        // Seed the queue with modules that have no dependencies (in-degree = 0)
        var queue = new Queue<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<IModule>(modules.Count);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            sorted.Add(modulesByName[name]);

            // Decrement in-degree for each dependent; enqueue when all dependencies are satisfied
            foreach (var dependent in dependents[name])
            {
                if (--inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        // If not all modules were emitted, the remaining ones form a cycle
        if (sorted.Count != modules.Count)
        {
            var cyclic = modules.Select(m => m.Name).Except(sorted.Select(m => m.Name));
            throw new InvalidOperationException(
                $"Circular dependency detected among modules: {string.Join(", ", cyclic)}");
        }

        return sorted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Module '{ModuleName}' is disabled — skipping registration")]
    private static partial void LogModuleDisabled(ILogger logger, string moduleName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registering module '{ModuleName}' (dependencies: {DependencyCount})")]
    private static partial void LogModuleRegistering(ILogger logger, string moduleName, int dependencyCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Module '{ModuleName}' registered in {DurationMs}ms")]
    private static partial void LogModuleRegistered(ILogger logger, string moduleName, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module '{ModuleName}' depends on '{DependencyName}' which is disabled — stub services will be used")]
    private static partial void LogDependencyDisabledWarning(ILogger logger, string moduleName, string dependencyName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan assembly '{AssemblyName}' for modules: {Error}")]
    private static partial void LogAssemblyScanFailed(ILogger logger, string assemblyName, string error);
}
