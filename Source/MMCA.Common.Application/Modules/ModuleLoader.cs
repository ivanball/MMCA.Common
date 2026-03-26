using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Modules;

/// <summary>
/// Discovers <see cref="IModule"/> implementations via reflection and registers them in
/// dependency order. Disabled modules receive stub service registrations so that
/// cross-module interfaces (e.g. <c>IProductVariantService</c>) remain resolvable.
/// </summary>
public sealed class ModuleLoader
{
    private readonly List<IModule> _enabledModules = [];
    private readonly List<string> _disabledModuleNames = [];

    /// <summary>Gets the modules that were successfully registered.</summary>
    public IReadOnlyList<IModule> EnabledModules => _enabledModules;

    /// <summary>Gets the names of modules that were skipped because they are disabled in configuration.</summary>
    public IReadOnlyList<string> DisabledModuleNames => _disabledModuleNames;

    /// <summary>
    /// Log callback: (level, message). Level is "Information" or "Warning".
    /// </summary>
    public Action<string, string>? Log { get; init; }

    /// <summary>
    /// Scans all loaded assemblies for <see cref="IModule"/> implementations, sorts them
    /// in dependency order via topological sort, and registers each enabled module into
    /// the DI container. Disabled modules receive stub registrations instead.
    /// </summary>
    /// <param name="services">The service collection to register module services into.</param>
    /// <param name="configurationBuilder">Allows modules to add their own configuration sources.</param>
    /// <param name="applicationSettings">Global application settings shared across modules.</param>
    /// <param name="modulesSettings">Per-module enabled/disabled configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a module with <see cref="IModule.RequiresDependencies"/> set to <see langword="true"/>
    /// has disabled dependencies.
    /// </exception>
    public void DiscoverAndRegister(
        IServiceCollection services,
        IConfigurationBuilder configurationBuilder,
        ApplicationSettings applicationSettings,
        ModulesSettings modulesSettings)
    {
        // Scan all loaded assemblies for concrete IModule implementations.
        // The try-catch guards against assemblies that throw on GetTypes()
        // (e.g. ReflectionTypeLoadException from missing transitive references).
        var moduleTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Warning", $"Failed to scan assembly '{a.FullName}' for modules: {ex.Message}");
                    return [];
                }
            })
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        var allModules = moduleTypes
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .ToList();

        // Topological sort ensures each module's dependencies are registered before the module itself
        var sorted = TopologicalSort(allModules);

        foreach (var module in sorted)
        {
            if (!modulesSettings.IsModuleEnabled(module.Name))
            {
                Log?.Invoke("Information", $"Module '{module.Name}' is disabled — skipping registration");
                module.RegisterDisabledStubs(services);
                _disabledModuleNames.Add(module.Name);
                continue;
            }

            // Validate dependencies
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
                Log?.Invoke("Warning",
                    $"Module '{module.Name}' depends on '{dependency}' which is disabled. " +
                    "Stub services will be used for cross-module calls");
            }

            Log?.Invoke("Information", $"Registering module '{module.Name}'");
            module.Register(services, configurationBuilder, applicationSettings);
            _enabledModules.Add(module);
        }
    }

    /// <summary>
    /// Invokes <see cref="IModule.SeedAsync"/> on each enabled module in registration order.
    /// </summary>
    /// <param name="serviceProvider">The root service provider for resolving seeder dependencies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all module seeders have finished.</returns>
    public async Task SeedAllAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        foreach (var module in _enabledModules)
        {
            await module.SeedAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
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
}
