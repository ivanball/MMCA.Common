using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Eagerly-built registry mapping every configured entity type to its physical data source.
/// Replaces the legacy lazy cache that was populated as a side effect of EF model building —
/// routing decisions (UnitOfWork, navigation classification, outbox enumeration) no longer
/// depend on a model having been built first.
/// </summary>
public interface IEntityDataSourceRegistry
{
    /// <summary>Gets the physical data source key for an entity type.</summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The physical data source backing the entity.</returns>
    /// <exception cref="InvalidOperationException">No entity type configuration registers the entity.</exception>
    DataSourceKey GetDataSourceKey(Type entityType);

    /// <summary>Gets the physical data source key for an entity by its full CLR type name.</summary>
    /// <param name="entityFullName">The entity's full CLR type name.</param>
    /// <returns>The physical data source backing the entity.</returns>
    /// <exception cref="InvalidOperationException">No entity type configuration registers the entity.</exception>
    DataSourceKey GetDataSourceKey(string entityFullName);

    /// <summary>Attempts to get the physical data source key for an entity by its full CLR type name.</summary>
    /// <param name="entityFullName">The entity's full CLR type name.</param>
    /// <param name="key">The physical data source key when registered.</param>
    /// <returns><see langword="true"/> when the entity is registered.</returns>
    bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key);

    /// <summary>
    /// Gets the distinct physical data sources used by the registered entities of this host.
    /// Used to enumerate databases for migrations, EnsureCreated, and outbox processing.
    /// </summary>
    IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse();
}
