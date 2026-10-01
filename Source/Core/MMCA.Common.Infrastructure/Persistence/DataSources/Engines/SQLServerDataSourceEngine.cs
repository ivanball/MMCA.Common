using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// SQL Server. The framework's default engine: always migrated, server-generated <c>rowversion</c>,
/// and the only engine that needs <c>SET IDENTITY_INSERT</c> for an explicit key, so it is its own
/// <see cref="IExplicitKeyInsertDialect"/>.
/// </summary>
internal sealed class SQLServerDataSourceEngine : IDataSourceEngine, IExplicitKeyInsertDialect
{
    // Empty options: all configuration (provider, connection, interceptors) happens in OnConfiguring.
    private static readonly DbContextOptions<SQLServerDbContext> Options =
        new DbContextOptionsBuilder<SQLServerDbContext>().Options;

    /// <inheritdoc />
    public DataSource Engine => DataSource.SQLServer;

    /// <inheritdoc />
    public int SubstitutionPriority => 0;

    /// <inheritdoc />
    public Type EntityConfigurationInterface => typeof(IEntityTypeConfigurationSQLServer<,>);

    /// <inheritdoc />
    public string? MigrationsAssemblySettingName => nameof(DataSourceEntrySettings.SQLServerMigrationsAssembly);

    /// <inheritdoc />
    public bool ConnectionIdentityIncludesDatabaseName => false;

    /// <inheritdoc />
    public DataSourceEngineCapabilities Capabilities { get; } = new(
        Migrations: MigrationPolicy.Always,
        ConnectionStringRequired: true,
        IsRelational: true,
        NullsSortFirstAscending: true,
        RowVersion: RowVersionStrategy.StoreGenerated);

    /// <inheritdoc />
    public IExplicitKeyInsertDialect? ExplicitKeyInsert => this;

    /// <inheritdoc />
    public string GetConnectionString(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SQLServerConnectionString;
    }

    /// <inheritdoc />
    public string GetConnectionString(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SQLServerConnectionString;
    }

    /// <inheritdoc />
    public string? GetConnectionString(TenantDataSourceOverrideSettings tenantOverride)
    {
        ArgumentNullException.ThrowIfNull(tenantOverride);
        return tenantOverride.SQLServerConnectionString;
    }

    /// <inheritdoc />
    public string GetTopLevelMigrationsAssembly(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SQLServerMigrationsAssembly;
    }

    /// <inheritdoc />
    public string GetMigrationsAssembly(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SQLServerMigrationsAssembly;
    }

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physical) =>
        new SQLServerDbContext(Options, serviceProvider, assemblyProvider, physical);

    /// <inheritdoc />
    public void ApplyKeyAndTableMapping<TEntity, TIdentifierType>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Shared verbatim with PostgreSQL (ADR-113): table + module schema.
        builder.ToTable(typeof(TEntity).Name, NamespaceConventions.GetModuleName(typeof(TEntity)) ?? "dbo");
        builder.HasKey(p => p.Id);
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).ValueGeneratedOnAdd();
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }

    /// <inheritdoc />
    public IndexBuilder IncludeColumns(IndexBuilder index, params string[] propertyNames) =>
        SqlServerIndexBuilderExtensions.IncludeProperties(index, propertyNames);

    /// <inheritdoc />
    public string QuoteColumn(string column) => $"[{column}]";

    /// <inheritdoc />
    public string? BuildSoftDeleteFilter(string isDeletedColumn) => $"[{isDeletedColumn}] = 0";

    /// <inheritdoc />
    public IReadOnlyList<ExplicitKeyInsertGroup> FindGroups(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var groups = new Dictionary<(string Schema, string Table), List<EntityEntry>>(2);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
                continue;

            var entityType = entry.Metadata;
            var pk = entityType.FindPrimaryKey();
            if (pk is null || pk.Properties.Count != 1)
                continue;

            var idProp = pk.Properties[0];
            if (SqlServerPropertyExtensions.GetValueGenerationStrategy(idProp)
                != SqlServerValueGenerationStrategy.IdentityColumn)
            {
                continue;
            }

            // EF assigns temporary values to identity keys left at default; only an explicitly set
            // (imported) value needs the toggle.
            if (entry.Property(idProp.Name).IsTemporary)
                continue;

            var key = (entityType.GetSchema() ?? "dbo", entityType.GetTableName()!);
            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups[key] = entries;
            }

            entries.Add(entry);
        }

        return [.. groups.Select(g => new ExplicitKeyInsertGroup(g.Key.Schema, g.Key.Table, g.Value))];
    }

    /// <inheritdoc />
    public string BuildToggleSql(string schema, string table, bool enable) =>
        string.Concat("SET IDENTITY_INSERT [", schema, "].[", table, enable ? "] ON" : "] OFF");
}
