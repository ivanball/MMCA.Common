using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Singleton <see cref="IPhysicalDbContextFactory"/> that constructs context instances directly,
/// resolving connection information through <see cref="IDataSourceResolver"/>. Each engine builds
/// its own sealed context (<see cref="IDataSourceEngine.CreateDbContext"/>) over empty options: all
/// configuration (provider, connection, interceptors, model cache key) happens in each context's
/// <c>OnConfiguring</c>.
/// <para>
/// IMPORTANT: these contexts must never be pooled (<c>AddPooledDbContextFactory</c>) — each
/// instance carries per-source constructor state (<see cref="PhysicalDataSource"/>), which pooling
/// would reuse across sources, silently pointing repositories at the wrong database.
/// </para>
/// </summary>
public sealed class PhysicalDbContextFactory(
    IServiceProvider serviceProvider,
    IDataSourceResolver resolver,
    IEntityConfigurationAssemblyProvider assemblyProvider) : IPhysicalDbContextFactory
{
    /// <inheritdoc />
    public ApplicationDbContext Create(DataSourceKey key) => Create(key, resolver.GetPhysical(key));

    /// <inheritdoc />
    public ApplicationDbContext Create(DataSourceKey key, PhysicalDataSource physicalDataSource)
    {
        var physical = physicalDataSource ?? throw new ArgumentNullException(nameof(physicalDataSource));

        return DataSourceEngines.For(key.Engine).CreateDbContext(serviceProvider, assemblyProvider, physical);
    }
}
