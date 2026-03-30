using System.Reflection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Default implementation that discovers entity configuration assemblies by scanning loaded assemblies
/// whose names end with ".Infrastructure". Also includes any additional assemblies explicitly registered
/// via <see cref="EntityConfigurationOptions"/> (e.g., Common.Infrastructure feature modules like Notification).
/// </summary>
public sealed class DefaultEntityConfigurationAssemblyProvider(
    IOptions<EntityConfigurationOptions> options) : IEntityConfigurationAssemblyProvider
{
    /// <inheritdoc />
    public IReadOnlyList<Assembly> GetConfigurationAssemblies() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName is not null
                && a.FullName.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)
                && !a.FullName.Contains("Common.Infrastructure", StringComparison.OrdinalIgnoreCase)),
         .. options.Value.AdditionalAssemblies.Distinct()];
}
