using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// SQLite entity configuration base. Maps entities to tables named after the entity type
/// and configures identity columns with auto-increment for supported key types.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="dataSourceService">Used to register which data source backs this entity.</param>
[UseDataSource(DataSource.Sqlite)]
public abstract class EntityTypeConfigurationSqlite<TEntity, TIdentifierType>(IDataSourceService dataSourceService)
    : EntityTypeConfigurationBase<TEntity, TIdentifierType>(dataSourceService),
      IEntityTypeConfigurationSqlite<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<TEntity> builder)
    {
        base.Configure(builder);

        builder.ToTable(typeof(TEntity).Name);

        builder.HasKey(p => p.Id);
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).ValueGeneratedOnAdd().UseIdentityColumn(1, 1);
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }
}
