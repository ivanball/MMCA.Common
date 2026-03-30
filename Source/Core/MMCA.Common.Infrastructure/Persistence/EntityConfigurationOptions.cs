using System.Reflection;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Options for registering additional assemblies containing EF Core entity type configurations.
/// Used by <see cref="DefaultEntityConfigurationAssemblyProvider"/> to include configurations
/// from assemblies that are not automatically discovered (e.g., Common.Infrastructure feature modules).
/// </summary>
public sealed class EntityConfigurationOptions
{
    /// <summary>
    /// Assemblies containing EF Core entity type configurations to include
    /// alongside the auto-discovered module infrastructure assemblies.
    /// </summary>
    public List<Assembly> AdditionalAssemblies { get; } = [];
}
