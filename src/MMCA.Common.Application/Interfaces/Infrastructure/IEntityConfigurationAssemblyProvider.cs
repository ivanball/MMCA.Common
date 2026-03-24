using System.Reflection;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Provides assemblies containing EF Core entity type configurations.
/// Used by <c>ApplicationDbContext</c> to discover and apply configurations without
/// hardcoding module assembly name patterns.
/// </summary>
public interface IEntityConfigurationAssemblyProvider
{
    /// <summary>
    /// Returns the assemblies that contain entity type configurations to apply during model creation.
    /// </summary>
    IReadOnlyList<Assembly> GetConfigurationAssemblies();
}
