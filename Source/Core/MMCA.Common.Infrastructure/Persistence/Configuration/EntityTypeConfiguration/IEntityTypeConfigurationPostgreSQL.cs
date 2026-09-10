using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Marker interface for PostgreSQL entity type configurations. Used by
/// <see cref="DbContexts.ApplicationDbContext.ApplyConfigurationsForEntitiesInContext"/>
/// to discover and apply only PostgreSQL-specific configurations.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal interface IEntityTypeConfigurationPostgreSQL<TEntity, TIdentifierType> : IEntityTypeConfigurationBase<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    new void Configure(EntityTypeBuilder<TEntity> builder);
}
