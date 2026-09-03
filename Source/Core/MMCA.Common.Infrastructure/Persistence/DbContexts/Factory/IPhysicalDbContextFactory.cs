using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Creates raw <see cref="ApplicationDbContext"/> instances for a specific physical data source.
/// The engine selects the context class (SQL Server / Cosmos / SQLite); the source name selects
/// the database (connection string, migrations assembly, EF model).
/// <para>
/// Contexts created here are not scoped or cached — <see cref="IDbContextFactory"/> layers
/// per-scope caching, save coordination, and transactions on top.
/// </para>
/// </summary>
public interface IPhysicalDbContextFactory
{
    /// <summary>
    /// Creates a new context instance for the given physical data source.
    /// </summary>
    /// <param name="key">The physical data source key.</param>
    /// <returns>A new context targeting that database.</returns>
    ApplicationDbContext Create(DataSourceKey key);

    /// <summary>
    /// Creates a new context for <paramref name="key"/> against explicitly supplied connection
    /// information rather than the resolver's, which is how database-per-tenant routing works: the
    /// caller clones the resolved <see cref="PhysicalDataSource"/> with the tenant's connection
    /// string and keeps the SAME <paramref name="key"/>, so EF's model cache still serves one model
    /// per (context type, source name) across every tenant.
    /// </summary>
    /// <param name="key">The physical data source key; decides the context class and the EF model.</param>
    /// <param name="physicalDataSource">
    /// The connection information to use. Its <see cref="PhysicalDataSource.Key"/> is expected to
    /// equal <paramref name="key"/>.
    /// </param>
    /// <returns>A new context targeting that database.</returns>
    ApplicationDbContext Create(DataSourceKey key, PhysicalDataSource physicalDataSource);
}
