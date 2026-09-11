using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Bridges the Application layer's <see cref="IRawSqlQueryExecutor"/> to EF Core's
/// <c>Database.SqlQuery&lt;T&gt;</c>, which accepts a <see cref="FormattableString"/> and turns every
/// interpolation hole into a command parameter (scalars and unmapped DTOs both).
/// <para>
/// The statement runs on the host's <b>default</b> physical data source, resolved the way the
/// framework's own tables resolve theirs: ask for SQL Server's <c>Default</c> source and let
/// <see cref="IDataSourceResolver"/> substitute the engine a single-engine host actually configured.
/// The context comes from the scoped <see cref="IDbContextFactory"/>, so the statement shares the
/// caller's connection and any transaction an <c>ITransactional</c> command opened.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Supplies the scope's context for the resolved source.</param>
/// <param name="dataSourceResolver">Resolves the default physical data source for this host.</param>
internal sealed class EFRawSqlQueryExecutor(
    IDbContextFactory dbContextFactory,
    IDataSourceResolver dataSourceResolver) : IRawSqlQueryExecutor
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync<T>(FormattableString sql, CancellationToken cancellationToken = default) =>
        await SqlQueryable<T>(sql).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<T?> QuerySingleOrDefaultAsync<T>(FormattableString sql, CancellationToken cancellationToken = default) =>
        await SqlQueryable<T>(sql).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Builds the parameterized queryable for one statement, after checking that the resolved source
    /// can run SQL at all.
    /// </summary>
    /// <typeparam name="T">The row shape.</typeparam>
    /// <param name="sql">The interpolated SQL statement.</param>
    /// <returns>The composable queryable EF produced for the statement.</returns>
    private IQueryable<T> SqlQueryable<T>(FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var dataSourceKey = dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName);
        if (dataSourceKey.Engine == DataSource.CosmosDB)
        {
            throw new NotSupportedException(
                "Raw SQL queries need a relational data source, and this host's default source is Cosmos DB. "
                + "Cosmos speaks its own query language and exposes no parameterized SQL command surface, so "
                + "express the read with LINQ (IQueryableExecutor) or move the entity to a relational source.");
        }

        return dbContextFactory.GetDbContext(dataSourceKey).Database.SqlQuery<T>(sql);
    }
}
