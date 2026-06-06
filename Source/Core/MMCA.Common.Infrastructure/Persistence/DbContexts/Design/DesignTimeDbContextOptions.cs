using System.Reflection;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

/// <summary>
/// Options for <see cref="DesignTimeDbContextHelper"/>. A downstream migrations project supplies
/// its module's configuration assemblies and connection settings; the data source name normally
/// comes from the <c>dotnet ef ... -- --datasource &lt;Name&gt;</c> argument.
/// </summary>
public sealed class DesignTimeDbContextOptions
{
    /// <summary>
    /// Gets or sets the logical data source name to build the context for. When
    /// <see langword="null"/>, the name is parsed from the <c>--datasource</c> design-time
    /// argument, falling back to <c>Default</c>.
    /// </summary>
    public string? DataSourceName { get; set; }

    /// <summary>
    /// Gets or sets the top-level connection strings (the <c>Default</c> source), including
    /// <c>SQLServerMigrationsAssembly</c>.
    /// </summary>
    public ConnectionStringSettings ConnectionStrings { get; set; } = new();

    /// <summary>Gets the named data source entries (mirrors the <c>DataSources</c> configuration section).</summary>
    public Dictionary<string, DataSourceEntrySettings> DataSources { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the assemblies containing the entity type configurations to include in the model.
    /// Must be listed explicitly — the AppDomain scan used at runtime sees nothing at design time.
    /// </summary>
    public IList<Assembly> ConfigurationAssemblies { get; } = [];

    /// <summary>Adds an assembly containing entity type configurations.</summary>
    /// <param name="assembly">The configuration assembly.</param>
    /// <returns>These options, for chaining.</returns>
    public DesignTimeDbContextOptions AddConfigurationAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!ConfigurationAssemblies.Contains(assembly))
        {
            ConfigurationAssemblies.Add(assembly);
        }

        return this;
    }
}
