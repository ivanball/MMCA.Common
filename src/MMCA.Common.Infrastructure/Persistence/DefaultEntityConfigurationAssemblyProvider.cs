using System.Reflection;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Default implementation that discovers entity configuration assemblies by scanning loaded assemblies
/// whose names end with ".Infrastructure". This replaces the previous hardcoded module prefix scan
/// and works with any module namespace prefix.
/// </summary>
public sealed class DefaultEntityConfigurationAssemblyProvider : IEntityConfigurationAssemblyProvider
{
    /// <inheritdoc />
    public IReadOnlyList<Assembly> GetConfigurationAssemblies() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName is not null
                && a.FullName.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)
                && !a.FullName.Contains("Common.Infrastructure", StringComparison.OrdinalIgnoreCase))];
}
