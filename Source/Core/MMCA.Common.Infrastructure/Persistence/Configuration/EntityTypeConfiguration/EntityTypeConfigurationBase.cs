using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Base class for all entity type configurations. Handles cross-cutting concerns:
/// excluding the <see cref="AuditableAggregateRootEntity{TId}.DomainEvents"/> collection from EF mapping.
/// <para>
/// The entity-to-data-source mapping is no longer registered here as a model-building side effect —
/// <see cref="DataSources.EntityDataSourceRegistry"/> derives it eagerly from the configuration
/// class's attributes (<see cref="UseDataSourceAttribute"/> for the engine,
/// <see cref="UseDatabaseAttribute"/>/module namespace for the database).
/// </para>
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public abstract class EntityTypeConfigurationBase<TEntity, TIdentifierType>
    : IEntityTypeConfigurationBase<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        // DomainEvents is an in-memory-only collection used for event dispatch;
        // it has no database column and must be excluded from EF mapping.
        if (typeof(IAggregateRoot).IsAssignableFrom(typeof(TEntity)))
        {
            builder.Ignore(nameof(AuditableAggregateRootEntity<>.DomainEvents));
        }
    }
}
