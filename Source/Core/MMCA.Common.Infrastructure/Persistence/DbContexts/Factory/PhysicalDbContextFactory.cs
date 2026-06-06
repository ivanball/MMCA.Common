using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Singleton <see cref="IPhysicalDbContextFactory"/> that constructs context instances directly,
/// resolving connection information through <see cref="IDataSourceResolver"/>.
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
    // Options are intentionally empty — all configuration (provider, connection, interceptors,
    // model cache key) happens in each context's OnConfiguring. Matches the empty options the
    // previous AddDbContextFactory<T>() registrations produced.
    private static readonly DbContextOptions<SQLServerDbContext> SqlServerOptions =
        new DbContextOptionsBuilder<SQLServerDbContext>().Options;

    private static readonly DbContextOptions<SqliteDbContext> SqliteOptions =
        new DbContextOptionsBuilder<SqliteDbContext>().Options;

    private static readonly DbContextOptions<CosmosDbContext> CosmosOptions =
        new DbContextOptionsBuilder<CosmosDbContext>().Options;

    /// <inheritdoc />
    public ApplicationDbContext Create(DataSourceKey key)
    {
        var physical = resolver.GetPhysical(key);

        return key.Engine switch
        {
            DataSource.SQLServer => new SQLServerDbContext(SqlServerOptions, serviceProvider, assemblyProvider, physical),
            DataSource.Sqlite => new SqliteDbContext(SqliteOptions, serviceProvider, assemblyProvider, physical),
            DataSource.CosmosDB => new CosmosDbContext(CosmosOptions, serviceProvider, assemblyProvider, physical),
            _ => throw new InvalidOperationException($"Invalid DataSource \"{key.Engine}\""),
        };
    }
}
