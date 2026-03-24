using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.ValueGenerators;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Cosmos DB entity configuration base. Maps entities in the same module to a shared container
/// (derived from the namespace segment after "Modules") and uses the entity Id as the partition key.
/// Cosmos DB lacks server-side identity columns, so <see cref="CosmosIntIdValueGenerator"/> provides
/// client-side integer ID generation for auto-increment key types.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="dataSourceService">Used to register which data source backs this entity.</param>
[UseDataSource(DataSource.CosmosDB)]
public abstract class EntityTypeConfigurationCosmos<TEntity, TIdentifierType>(IDataSourceService dataSourceService)
    : EntityTypeConfigurationBase<TEntity, TIdentifierType>(dataSourceService),
      IEntityTypeConfigurationCosmos<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<TEntity> builder)
    {
        base.Configure(builder);

        // All entities in the same module share one Cosmos container so that
        // navigation properties (HasOne/WithMany) work correctly — Cosmos requires
        // related documents to reside in the same container.
        var segments = typeof(TEntity).Namespace?.Split('.') ?? [];
        var modulesIndex = Array.FindIndex(segments,
            s => s.Equals("Modules", StringComparison.OrdinalIgnoreCase));
        var containerName = modulesIndex >= 0 && modulesIndex + 1 < segments.Length
            ? segments[modulesIndex + 1]
            : typeof(TEntity).Name;

        // Partition key = entity Id: simple and guarantees single-partition reads for point queries.
        builder.ToContainer(containerName)
            .HasPartitionKey(p => p.Id);

        builder.HasKey(p => p.Id);

        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).HasValueGenerator<CosmosIntIdValueGenerator>();
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }
}
