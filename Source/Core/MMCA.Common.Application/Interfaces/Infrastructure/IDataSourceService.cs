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
/// Resolves which physical data source (<see cref="DataSourceKey"/>: engine + database) backs a
/// given entity type and determines whether two entity types share a data source that supports
/// EF Core <c>.Include()</c>. Used by <see cref="Services.Query.NavigationMetadataProvider"/> to
/// classify navigation properties as supported or unsupported includes.
/// </summary>
public interface IDataSourceService
{
    /// <summary>Gets the physical data source key for an entity by its CLR type.</summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The physical data source backing this entity.</returns>
    DataSourceKey GetDataSourceKey(Type entityType);

    /// <summary>Gets the physical data source key for an entity by its full type name.</summary>
    /// <param name="entityFullName">The entity's full CLR type name.</param>
    /// <returns>The physical data source backing this entity.</returns>
    DataSourceKey GetDataSourceKey(string entityFullName);

    /// <summary>Gets the database engine for an entity by its full type name.</summary>
    /// <param name="entityFullName">The entity's full CLR type name.</param>
    /// <returns>The engine backing this entity.</returns>
    DataSource GetDataSource(string entityFullName);

    /// <summary>Gets the database engine for an entity by its CLR type.</summary>
    /// <param name="entityType">The entity CLR type.</param>
    /// <returns>The engine backing this entity.</returns>
    DataSource GetDataSource(Type entityType);

    /// <summary>
    /// Determines whether two physical data sources support EF Core <c>.Include()</c> between them.
    /// Returns <see langword="true"/> only when both keys identify the same physical database
    /// and the engine is relational (Cosmos DB does not support cross-document JOINs).
    /// </summary>
    /// <param name="first">The first physical data source.</param>
    /// <param name="second">The second physical data source.</param>
    /// <returns><see langword="true"/> if JOINs/includes are supported between the two sources.</returns>
    bool HaveIncludeSupport(DataSourceKey first, DataSourceKey second);

    /// <summary>
    /// Determines whether two entity types support EF Core <c>.Include()</c> between them
    /// by resolving their physical data sources.
    /// </summary>
    /// <param name="firstEntityFullName">Full type name of the first entity.</param>
    /// <param name="secondEntityFullName">Full type name of the second entity.</param>
    /// <returns><see langword="true"/> if JOINs/includes are supported between the two entities.</returns>
    bool HaveIncludeSupport(string firstEntityFullName, string secondEntityFullName);
}
