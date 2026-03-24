using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Base class for all entity type configurations. Handles cross-cutting concerns:
/// registering the entity's data source in the cache, and excluding the
/// <see cref="AuditableAggregateRootEntity{TId}.DomainEvents"/> collection from EF mapping.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="dataSourceService">Used to register which data source backs this entity.</param>
public abstract class EntityTypeConfigurationBase<TEntity, TIdentifierType>(IDataSourceService dataSourceService)
    : IEntityTypeConfigurationBase<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        // Side effect: registers the entity-to-DataSource mapping in the cache so
        // UnitOfWork can later resolve the correct DbContext for this entity type.
        _ = dataSourceService.GetDataSource(typeof(TEntity), GetType());

        // DomainEvents is an in-memory-only collection used for event dispatch;
        // it has no database column and must be excluded from EF mapping.
        if (typeof(IAggregateRoot).IsAssignableFrom(typeof(TEntity)))
        {
            builder.Ignore(nameof(AuditableAggregateRootEntity<>.DomainEvents));
        }
    }
}
