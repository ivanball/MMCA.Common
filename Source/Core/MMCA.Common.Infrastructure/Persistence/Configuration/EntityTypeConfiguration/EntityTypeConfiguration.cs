using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.ValueGenerators;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Engine-aware entity type configuration base. The target engine is declared once via a
/// <see cref="UseDataSourceAttribute"/> on the concrete configuration (or an inherited shim base);
/// this base reads that attribute and applies the matching mapping conventions (table + schema for
/// SQL Server, table for SQLite, container + partition key for Cosmos) plus key generation. Moving an
/// entity between engines is therefore a single attribute change with no configuration-body edits —
/// the framework also strips relational-only constructs (Cosmos indexes) and degrades cross-source
/// relationships automatically, so the same body is portable across engines.
/// <para>
/// It implements all three provider marker interfaces so it is discovered for every engine's model
/// pass; <see cref="DbContexts.ApplicationDbContext.ApplyConfigurationsForEntitiesInContext"/> then
/// applies it only to the model whose physical data source the entity actually routes to (driven by
/// the same <see cref="UseDataSourceAttribute"/>), so discovery and routing agree by construction.
/// </para>
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public abstract class EntityTypeConfiguration<TEntity, TIdentifierType>
    : EntityTypeConfigurationBase<TEntity, TIdentifierType>,
      IEntityTypeConfigurationSQLServer<TEntity, TIdentifierType>,
      IEntityTypeConfigurationSqlite<TEntity, TIdentifierType>,
      IEntityTypeConfigurationCosmos<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<TEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.Configure(builder);

        var engine = GetType().GetCustomAttribute<UseDataSourceAttribute>()?.DataSource
            ?? throw new InvalidOperationException(
                $"Configuration '{GetType().Name}' must be annotated with [UseDataSource(...)] " +
                "(directly or via a provider base class) so its target engine is known.");

        ApplyEngineConventions(builder, engine);
    }

    /// <summary>
    /// Applies the engine-specific table/container mapping and key generation. Extracted as a
    /// protected static helper so the provider shim bases can share the exact same logic.
    /// </summary>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="engine">The target data source engine.</param>
    protected static void ApplyEngineConventions(EntityTypeBuilder<TEntity> builder, DataSource engine)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var isIdValueGenerated = typeof(TEntity).IsIdValueGenerated;

        switch (engine)
        {
            case DataSource.SQLServer:
                builder.ToTable(typeof(TEntity).Name, NamespaceConventions.GetModuleName(typeof(TEntity)) ?? "dbo");
                builder.HasKey(p => p.Id);
                if (isIdValueGenerated)
                    builder.Property(p => p.Id).ValueGeneratedOnAdd();
                else
                    builder.Property(p => p.Id).ValueGeneratedNever();
                break;

            case DataSource.Sqlite:
                builder.ToTable(typeof(TEntity).Name);
                builder.HasKey(p => p.Id);
                if (isIdValueGenerated)
                    builder.Property(p => p.Id).ValueGeneratedOnAdd().UseIdentityColumn(1, 1);
                else
                    builder.Property(p => p.Id).ValueGeneratedNever();
                break;

            case DataSource.CosmosDB:
                // All of a module's entities share one container (segment before "Domain"), so their
                // relationships and the navigation populators work; the entity Id is the partition key.
                builder
                    .ToContainer(NamespaceConventions.GetModuleName(typeof(TEntity)) ?? typeof(TEntity).Name)
                    .HasPartitionKey(p => p.Id);
                builder.HasKey(p => p.Id);
                if (isIdValueGenerated)
                    builder.Property(p => p.Id).HasValueGenerator<CosmosIntIdValueGenerator>();
                else
                    builder.Property(p => p.Id).ValueGeneratedNever();
                break;

            default:
                throw new InvalidOperationException($"DataSource \"{engine}\" not implemented.");
        }
    }
}
