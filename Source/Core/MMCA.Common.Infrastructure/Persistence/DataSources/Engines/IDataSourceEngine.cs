using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// Everything the framework needs to know about one database engine, so that no call site branches on
/// <see cref="DataSource"/>. Instances are stateless singletons looked up through
/// <see cref="DataSourceEngines.For(DataSource)"/>.
/// <para>
/// Members fall in three groups: DESCRIPTIVE facts (configuration shape, types), <see cref="Capabilities"/> (plain
/// values), and BEHAVIOUR hooks (the few places where the engine has to act, not just answer).
/// </para>
/// </summary>
internal interface IDataSourceEngine
{
    /// <summary>Gets the enum value this engine answers for (the shipped ordinal stays the identity).</summary>
    DataSource Engine { get; }

    // ---- DESCRIPTIVE ---------------------------------------------------------------------------

    /// <summary>
    /// Gets the engine's rank when the resolver substitutes a configured engine for an unconfigured
    /// one (lower wins). Replaces the hand-ordered <c>EnginePreference</c> array.
    /// </summary>
    int SubstitutionPriority { get; }

    /// <summary>Gets the open generic entity-configuration interface whose implementations belong to this engine's model.</summary>
    Type EntityConfigurationInterface { get; }

    /// <summary>
    /// Gets the configuration key that names this engine's migrations assembly, for error messages;
    /// <see langword="null"/> when the engine migrates nothing.
    /// </summary>
    string? MigrationsAssemblySettingName { get; }

    /// <summary>
    /// Gets a value indicating whether two sources are the same physical database only when they also
    /// name the same database (Cosmos: one account hosts many), rather than by connection string alone.
    /// </summary>
    bool ConnectionIdentityIncludesDatabaseName { get; }

    /// <summary>Reads this engine's connection string from the top-level <c>ConnectionStrings</c> section.</summary>
    /// <param name="settings">The bound top-level section.</param>
    /// <returns>The connection string, empty when not configured.</returns>
    string GetConnectionString(ConnectionStringSettings settings);

    /// <summary>Reads this engine's connection string from one named <c>DataSources</c> entry.</summary>
    /// <param name="entry">The bound entry.</param>
    /// <returns>The connection string, empty when not configured.</returns>
    string GetConnectionString(DataSourceEntrySettings entry);

    /// <summary>Reads this engine's connection string from one tenant override.</summary>
    /// <param name="tenantOverride">The bound tenant override.</param>
    /// <returns>The connection string, or <see langword="null"/> when the override declares none.</returns>
    string? GetConnectionString(TenantDataSourceOverrideSettings tenantOverride);

    /// <summary>Reads this engine's own top-level migrations assembly (empty when the section carries none for it).</summary>
    /// <param name="settings">The bound top-level section.</param>
    /// <returns>The assembly name, or an empty string.</returns>
    string GetTopLevelMigrationsAssembly(ConnectionStringSettings settings);

    /// <summary>Reads this engine's migrations assembly from one named <c>DataSources</c> entry.</summary>
    /// <param name="entry">The bound entry.</param>
    /// <returns>The assembly name, or an empty string.</returns>
    string GetMigrationsAssembly(DataSourceEntrySettings entry);

    // ---- CAPABILITIES --------------------------------------------------------------------------

    /// <summary>Gets what the engine can and cannot do.</summary>
    DataSourceEngineCapabilities Capabilities { get; }

    // ---- BEHAVIOUR -----------------------------------------------------------------------------

    /// <summary>Creates this engine's sealed context over one physical source (never pooled).</summary>
    /// <param name="serviceProvider">The root service provider the context resolves its collaborators from.</param>
    /// <param name="assemblyProvider">Supplies the entity-configuration assemblies.</param>
    /// <param name="physical">The physical source the context connects to.</param>
    /// <returns>A new context instance.</returns>
    ApplicationDbContext CreateDbContext(
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physical);

    /// <summary>
    /// Applies the engine's table or container mapping and key generation to one entity (the body of
    /// <c>EntityTypeConfiguration.ApplyEngineConventions</c>).
    /// </summary>
    /// <typeparam name="TEntity">The entity type being configured.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    void ApplyKeyAndTableMapping<TEntity, TIdentifierType>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull;

    /// <summary>Adds covering (INCLUDE) columns to an index with this engine's provider annotation.</summary>
    /// <param name="index">The index being configured.</param>
    /// <param name="propertyNames">The names of the columns to include.</param>
    /// <returns>The same index builder, for chaining.</returns>
    IndexBuilder IncludeColumns(IndexBuilder index, params string[] propertyNames);

    /// <summary>Quotes one column name the way this engine expects inside a filtered-index predicate.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The quoted identifier.</returns>
    string QuoteColumn(string column);

    /// <summary>Builds the soft-delete index predicate for the given column.</summary>
    /// <param name="isDeletedColumn">The mapped name of the soft-delete column.</param>
    /// <returns>The predicate SQL, or <see langword="null"/> where filtered indexes are unsupported.</returns>
    string? BuildSoftDeleteFilter(string isDeletedColumn);

    /// <summary>
    /// Gets the explicit-key insert dialect, or <see langword="null"/> when the engine accepts an
    /// explicit value for a store-generated key without a toggle (the save then runs unchanged).
    /// </summary>
    IExplicitKeyInsertDialect? ExplicitKeyInsert { get; }
}
