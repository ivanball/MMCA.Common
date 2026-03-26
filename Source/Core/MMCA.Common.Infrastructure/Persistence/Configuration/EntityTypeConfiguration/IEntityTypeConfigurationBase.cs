using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Base marker interface for all entity type configurations. Extends EF Core's
/// <see cref="IEntityTypeConfiguration{TEntity}"/> with an explicit <c>Configure</c> method
/// so provider-specific interfaces can redeclare it via <see langword="new"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityTypeConfigurationBase<TEntity, TIdentifierType> : IEntityTypeConfiguration<TEntity>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    new void Configure(EntityTypeBuilder<TEntity> builder);
}
