using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Application-facing facade over <see cref="IEntityDataSourceRegistry"/>. Resolves each entity's
/// physical data source (engine + database) and answers include-support questions for navigation
/// classification. The registry is built eagerly from configuration assemblies, so resolution no
/// longer depends on an EF model having been built first.
/// </summary>
public sealed class DataSourceService(IEntityDataSourceRegistry registry) : IDataSourceService
{
    /// <inheritdoc />
    public DataSourceKey GetDataSourceKey(Type entityType) => registry.GetDataSourceKey(entityType);

    /// <inheritdoc />
    public DataSourceKey GetDataSourceKey(string entityFullName) => registry.GetDataSourceKey(entityFullName);

    /// <inheritdoc />
    public DataSource GetDataSource(string entityFullName) => registry.GetDataSourceKey(entityFullName).Engine;

    /// <inheritdoc />
    public DataSource GetDataSource(Type entityType) => registry.GetDataSourceKey(entityType).Engine;

    /// <inheritdoc />
    /// <remarks>
    /// EF Include (eager loading) only works when both entities live in the same physical database
    /// on a relational engine. Cosmos DB does not support cross-document includes.
    /// </remarks>
    public bool HaveIncludeSupport(DataSourceKey first, DataSourceKey second)
        => first == second && first.Engine != DataSource.CosmosDB;

    /// <inheritdoc />
    public bool HaveIncludeSupport(string firstEntityFullName, string secondEntityFullName)
        => registry.TryGetDataSourceKey(firstEntityFullName, out var first)
            && registry.TryGetDataSourceKey(secondEntityFullName, out var second)
            && HaveIncludeSupport(first, second);
}
