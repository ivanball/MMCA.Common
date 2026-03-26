using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Identifies which database backend an entity type is persisted to.
/// </summary>
public enum DataSource
{
    /// <summary>Azure Cosmos DB (document store, no cross-container JOINs).</summary>
    CosmosDB,

    /// <summary>SQLite (supports JOINs within a single database file).</summary>
    Sqlite,

    /// <summary>SQL Server (full relational JOIN support).</summary>
    SQLServer
}

/// <summary>
/// Resolves which <see cref="DataSource"/> backs a given entity type and determines
/// whether two entity types share a data source that supports EF Core <c>.Include()</c>.
/// Used by <see cref="Services.Query.NavigationMetadataProvider"/> to classify navigation
/// properties as supported or unsupported includes.
/// </summary>
public interface IDataSourceService
{
    /// <summary>Gets the data source for an entity based on its type and EF configuration type.</summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <param name="configurationType">The EF entity type configuration CLR type.</param>
    /// <returns>The data source backing this entity.</returns>
    DataSource GetDataSource(Type entityType, Type configurationType);

    /// <summary>Gets the data source for an entity using generic type parameters.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <typeparam name="TEntityTypeConfiguration">The EF entity type configuration.</typeparam>
    /// <returns>The data source backing this entity.</returns>
    DataSource GetDataSource<TEntity, TIdentifierType, TEntityTypeConfiguration>()
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TEntityTypeConfiguration : class
        where TIdentifierType : notnull;

    /// <summary>Gets the data source for an entity by its full type name.</summary>
    /// <param name="entityFullName">The entity's full CLR type name.</param>
    /// <returns>The data source backing this entity.</returns>
    DataSource GetDataSource(string entityFullName);

    /// <summary>Gets the data source for an entity by its CLR type.</summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The data source backing this entity.</returns>
    DataSource GetDataSource(Type entityType);

    /// <summary>
    /// Determines whether two data sources support EF Core <c>.Include()</c> between them.
    /// Returns <see langword="true"/> when both use the same relational provider (e.g. both SQL Server).
    /// </summary>
    /// <param name="first">The first data source.</param>
    /// <param name="second">The second data source.</param>
    /// <returns><see langword="true"/> if JOINs/includes are supported between the two sources.</returns>
    bool HaveIncludeSupport(DataSource first, DataSource second);

    /// <summary>
    /// Determines whether two entity types support EF Core <c>.Include()</c> between them
    /// by resolving their data sources.
    /// </summary>
    /// <param name="firstEntityFullName">Full type name of the first entity.</param>
    /// <param name="secondEntityFullName">Full type name of the second entity.</param>
    /// <returns><see langword="true"/> if JOINs/includes are supported between the two entities.</returns>
    bool HaveIncludeSupport(string firstEntityFullName, string secondEntityFullName);
}
