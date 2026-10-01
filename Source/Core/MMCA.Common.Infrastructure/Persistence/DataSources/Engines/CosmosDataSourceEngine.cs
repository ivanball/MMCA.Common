using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Persistence.ValueGenerators;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// Azure Cosmos DB. The only non-relational engine: no migrations, no JOIN-based include, no
/// transactions, no raw SQL, no indexes or FK constraints, no framework tables, no <c>Any</c>
/// translation, and a physical identity that includes the database name.
/// </summary>
internal sealed class CosmosDataSourceEngine : IDataSourceEngine
{
    // Empty options: all configuration (provider, connection, interceptors) happens in OnConfiguring.
    private static readonly DbContextOptions<CosmosDbContext> Options =
        new DbContextOptionsBuilder<CosmosDbContext>().Options;

    /// <inheritdoc />
    public DataSource Engine => DataSource.CosmosDB;

    /// <inheritdoc />
    public int SubstitutionPriority => 3;

    /// <inheritdoc />
    public Type EntityConfigurationInterface => typeof(IEntityTypeConfigurationCosmos<,>);

    /// <inheritdoc />
    /// <remarks><see langword="null"/>: Cosmos migrates nothing, so it has no such setting.</remarks>
    public string? MigrationsAssemblySettingName => null;

    /// <inheritdoc />
    public bool ConnectionIdentityIncludesDatabaseName => true;

    /// <inheritdoc />
    public DataSourceEngineCapabilities Capabilities { get; } = new(
        Migrations: MigrationPolicy.Never,
        ConnectionStringRequired: false,
        IsRelational: false,
        NullsSortFirstAscending: true,
        RowVersion: RowVersionStrategy.None);

    /// <inheritdoc />
    public IExplicitKeyInsertDialect? ExplicitKeyInsert => null;

    /// <inheritdoc />
    public string GetConnectionString(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.CosmosConnectionString;
    }

    /// <inheritdoc />
    public string GetConnectionString(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.CosmosConnectionString;
    }

    /// <inheritdoc />
    public string? GetConnectionString(TenantDataSourceOverrideSettings tenantOverride)
    {
        ArgumentNullException.ThrowIfNull(tenantOverride);
        return tenantOverride.CosmosConnectionString;
    }

    /// <inheritdoc />
    public string GetTopLevelMigrationsAssembly(ConnectionStringSettings settings) => string.Empty;

    /// <inheritdoc />
    public string GetMigrationsAssembly(DataSourceEntrySettings entry) => string.Empty;

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physical) =>
        new CosmosDbContext(Options, serviceProvider, assemblyProvider, physical);

    /// <inheritdoc />
    public void ApplyKeyAndTableMapping<TEntity, TIdentifierType>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        // All of a module's entities share one container, so relationships and the navigation
        // populators work; the entity Id is the partition key.
        builder
            .ToContainer(NamespaceConventions.GetModuleName(typeof(TEntity)) ?? typeof(TEntity).Name)
            .HasPartitionKey(p => p.Id);
        builder.HasKey(p => p.Id);
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).HasValueGenerator<CosmosIntIdValueGenerator>();
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The SQL Server annotation, like every engine but PostgreSQL. Unreachable in practice: Cosmos
    /// never maps the outbox or internal-command tables that call it.
    /// </remarks>
    public IndexBuilder IncludeColumns(IndexBuilder index, params string[] propertyNames) =>
        SqlServerIndexBuilderExtensions.IncludeProperties(index, propertyNames);

    /// <inheritdoc />
    /// <remarks>Brackets; only reached by a filter that <see cref="BuildSoftDeleteFilter"/> then discards.</remarks>
    public string QuoteColumn(string column) => $"[{column}]";

    /// <inheritdoc />
    public string? BuildSoftDeleteFilter(string isDeletedColumn) => null;
}
