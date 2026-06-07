using MMCA.Common.Application.Interfaces.Infrastructure;

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
}
