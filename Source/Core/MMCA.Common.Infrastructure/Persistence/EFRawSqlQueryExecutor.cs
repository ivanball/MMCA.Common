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
/// <para>
/// Registered only when that default source is on a relational engine (Cosmos DB speaks its own
/// query language and exposes no parameterized SQL command surface), so a Cosmos-default host that
/// injects <see cref="IRawSqlQueryExecutor"/> fails at resolution rather than on its first
/// statement (at container build in Development, where the default host validates the container;
/// on first resolution elsewhere).
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
    /// Builds the parameterized queryable for one statement on the host's default source.
    /// </summary>
    /// <typeparam name="T">The row shape.</typeparam>
    /// <param name="sql">The interpolated SQL statement.</param>
    /// <returns>The composable queryable EF produced for the statement.</returns>
    private IQueryable<T> SqlQueryable<T>(FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var dataSourceKey = dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName);
        return dbContextFactory.GetDbContext(dataSourceKey).Database.SqlQuery<T>(sql);
    }
}
