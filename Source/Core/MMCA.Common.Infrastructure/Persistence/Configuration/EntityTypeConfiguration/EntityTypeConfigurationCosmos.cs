using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Azure Cosmos DB entity configuration base — a thin shim that fixes the engine to
/// <see cref="DataSource.CosmosDB"/>. All mapping logic (per-module container, entity-Id partition
/// key, client-side Id generation) lives in the engine-aware
/// <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/>, which reads the
/// <see cref="UseDataSourceAttribute"/> this shim carries. Deriving from this base is equivalent to
/// deriving from <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/> and annotating the
/// concrete class with <c>[UseDataSource(DataSource.CosmosDB)]</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
[UseDataSource(DataSource.CosmosDB)]
public abstract class EntityTypeConfigurationCosmos<TEntity, TIdentifierType>
    : EntityTypeConfiguration<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
