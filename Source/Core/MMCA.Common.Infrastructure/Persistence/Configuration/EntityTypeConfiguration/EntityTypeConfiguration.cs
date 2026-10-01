using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// Engine-aware entity type configuration base. The target engine is declared once via a
/// <see cref="UseDataSourceAttribute"/> on the concrete configuration (or an inherited shim base);
/// this base reads that attribute and applies the matching mapping conventions (table + schema for
/// SQL Server and PostgreSQL, table for SQLite, container + partition key for Cosmos) plus key
/// generation. Moving an
/// entity between engines is therefore a single attribute change with no configuration-body edits —
/// the framework also strips relational-only constructs (Cosmos indexes) and degrades cross-source
/// relationships automatically, so the same body is portable across engines.
/// <para>
/// It implements all four provider marker interfaces so it is discovered for every engine's model
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
      IEntityTypeConfigurationPostgreSQL<TEntity, TIdentifierType>,
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

        EffectiveEngine = builder.Metadata.Model.FindAnnotation(DbContexts.ApplicationDbContext.EngineAnnotation)?.Value as DataSource?
            ?? engine;

        ApplyEngineConventions(builder, engine);
    }

    /// <summary>
    /// Gets the engine of the model being built. It equals the engine the configuration declares
    /// except when the host substitutes an unconfigured engine (for example a SQL Server
    /// configuration applied to a PostgreSQL-only host), so hand-written SQL such as an index
    /// filter must be produced for this engine, not for the declared one. Valid inside
    /// <see cref="Configure(EntityTypeBuilder{TEntity})"/> after the base call.
    /// </summary>
    protected DataSource EffectiveEngine { get; private set; }

    /// <summary>
    /// Applies the engine-specific table/container mapping and key generation. Extracted as a
    /// protected static helper so the provider shim bases can share the exact same logic.
    /// </summary>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="engine">The target data source engine.</param>
    protected static void ApplyEngineConventions(EntityTypeBuilder<TEntity> builder, DataSource engine)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Each engine owns its mapping (IDataSourceEngine.ApplyKeyAndTableMapping). PostgreSQL takes
        // the SQL Server mapping unchanged, module schema and PascalCase identifiers included, so an
        // entity moves between the two by changing its configuration base class and nothing else
        // (ADR-113); Cosmos puts all of a module's entities in one container, partitioned by Id.
        DataSourceEngines.For(engine).ApplyKeyAndTableMapping<TEntity, TIdentifierType>(builder);
    }
}
