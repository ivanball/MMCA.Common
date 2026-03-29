namespace MMCA.Common.Application.Modules;

/// <summary>
/// Seeds initial data for a module at application startup.
/// Implementations are auto-discovered by <see cref="ModuleLoader"/> and executed
/// in module dependency order after all modules have been registered.
/// </summary>
public interface IModuleSeeder
{
    /// <summary>
    /// The module name this seeder belongs to. Must match the corresponding <see cref="IModule.Name"/>.
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Seeds module data at startup. Called only for enabled modules after all modules are registered.
    /// </summary>
    Task SeedAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken);
}
