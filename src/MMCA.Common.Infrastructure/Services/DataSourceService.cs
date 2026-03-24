using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Maintains a singleton cache mapping entity type names to their <see cref="DataSource"/>.
/// Populated lazily during EF model configuration via <see cref="UseDataSourceAttribute"/> on configuration classes.
/// </summary>
public sealed class DataSourceService : IDataSourceService
{
    private readonly ConcurrentDictionary<string, DataSource> _dataSourceCache = new();

    /// <inheritdoc />
    public DataSource GetDataSource(Type entityType, Type configurationType)
        => _dataSourceCache.GetOrAdd(
                entityType.FullName!,
                _ => configurationType.GetCustomAttribute<UseDataSourceAttribute>()?.DataSource
                    ?? throw new InvalidOperationException($"DataSource not defined for {entityType.FullName}"));

    /// <inheritdoc />
    public DataSource GetDataSource<TEntity, TIdentifierType, TEntityTypeConfiguration>()
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TEntityTypeConfiguration : class
        where TIdentifierType : notnull
        => GetDataSource(typeof(TEntity), typeof(TEntityTypeConfiguration));

    /// <inheritdoc />
    public DataSource GetDataSource(string entityFullName)
        => _dataSourceCache.TryGetValue(entityFullName, out DataSource dataSource)
            ? dataSource
            : throw new InvalidOperationException($"DataSource not defined for {entityFullName}");

    /// <inheritdoc />
    public DataSource GetDataSource(Type entityType)
        => GetDataSource(entityType.FullName!);

    /// <inheritdoc />
    /// <remarks>
    /// EF Include (eager loading) only works when both entities share the same relational data source.
    /// Cosmos DB does not support cross-document includes — it models relationships differently.
    /// </remarks>
    public bool HaveIncludeSupport(DataSource first, DataSource second)
        => first == second && first != DataSource.CosmosDB;

    /// <inheritdoc />
    public bool HaveIncludeSupport(string firstEntityFullName, string secondEntityFullName)
        => _dataSourceCache.TryGetValue(firstEntityFullName, out var first)
            && _dataSourceCache.TryGetValue(secondEntityFullName, out var second)
            && HaveIncludeSupport(first, second);
}
