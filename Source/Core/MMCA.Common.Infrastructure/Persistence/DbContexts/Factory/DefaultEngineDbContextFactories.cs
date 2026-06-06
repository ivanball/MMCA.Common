using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Adapters preserving EF Core's <see cref="IDbContextFactory{TContext}"/> DI surface for the
/// three engine context types after the move to per-physical-source instantiation. Each returns
/// a context for the engine's <b>Default</b> physical source (the top-level connection strings),
/// matching the pre-multi-database behavior for consumers such as
/// <see cref="ApplicationDbContextEFFactory"/> and health checks.
/// </summary>
internal sealed class DefaultSqlServerDbContextFactory(IPhysicalDbContextFactory physicalFactory)
    : IDbContextFactory<SQLServerDbContext>
{
    /// <inheritdoc />
    public SQLServerDbContext CreateDbContext() =>
        (SQLServerDbContext)physicalFactory.Create(DataSourceKey.Default(DataSource.SQLServer));
}

/// <summary>Default-source factory adapter for <see cref="SqliteDbContext"/>.</summary>
internal sealed class DefaultSqliteDbContextFactory(IPhysicalDbContextFactory physicalFactory)
    : IDbContextFactory<SqliteDbContext>
{
    /// <inheritdoc />
    public SqliteDbContext CreateDbContext() =>
        (SqliteDbContext)physicalFactory.Create(DataSourceKey.Default(DataSource.Sqlite));
}

/// <summary>Default-source factory adapter for <see cref="CosmosDbContext"/>.</summary>
internal sealed class DefaultCosmosDbContextFactory(IPhysicalDbContextFactory physicalFactory)
    : IDbContextFactory<CosmosDbContext>
{
    /// <inheritdoc />
    public CosmosDbContext CreateDbContext() =>
        (CosmosDbContext)physicalFactory.Create(DataSourceKey.Default(DataSource.CosmosDB));
}
