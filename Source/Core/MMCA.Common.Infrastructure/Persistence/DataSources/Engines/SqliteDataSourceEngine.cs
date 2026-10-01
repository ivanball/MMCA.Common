using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// SQLite. No schema, no top-level migrations assembly, double-quoted identifiers, stamps its own row
/// version, and keeps the SQL Server index annotations (the provider ignores them).
/// </summary>
internal sealed class SqliteDataSourceEngine : IDataSourceEngine
{
    // Empty options: all configuration (provider, connection, interceptors) happens in OnConfiguring.
    private static readonly DbContextOptions<SqliteDbContext> Options =
        new DbContextOptionsBuilder<SqliteDbContext>().Options;

    /// <inheritdoc />
    public DataSource Engine => DataSource.Sqlite;

    /// <inheritdoc />
    public int SubstitutionPriority => 2;

    /// <inheritdoc />
    public Type EntityConfigurationInterface => typeof(IEntityTypeConfigurationSqlite<,>);

    /// <inheritdoc />
    public string? MigrationsAssemblySettingName => nameof(DataSourceEntrySettings.SqliteMigrationsAssembly);

    /// <inheritdoc />
    public bool ConnectionIdentityIncludesDatabaseName => false;

    /// <inheritdoc />
    public DataSourceEngineCapabilities Capabilities { get; } = new(
        Migrations: MigrationPolicy.WhenAssemblyConfigured,
        ConnectionStringRequired: false,
        IsRelational: true,
        NullsSortFirstAscending: true,
        RowVersion: RowVersionStrategy.ClientStamped);

    /// <inheritdoc />
    public IExplicitKeyInsertDialect? ExplicitKeyInsert => null;

    /// <inheritdoc />
    public string GetConnectionString(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SqliteConnectionString;
    }

    /// <inheritdoc />
    public string GetConnectionString(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SqliteConnectionString;
    }

    /// <inheritdoc />
    public string? GetConnectionString(TenantDataSourceOverrideSettings tenantOverride)
    {
        ArgumentNullException.ThrowIfNull(tenantOverride);
        return tenantOverride.SqliteConnectionString;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always empty: <c>ConnectionStrings</c> carries no SQLite migrations assembly, so a mixed-engine
    /// host can never hand SQLite the SQL Server snapshot.
    /// </remarks>
    public string GetTopLevelMigrationsAssembly(ConnectionStringSettings settings) => string.Empty;

    /// <inheritdoc />
    public string GetMigrationsAssembly(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SqliteMigrationsAssembly;
    }

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physical) =>
        new SqliteDbContext(Options, serviceProvider, assemblyProvider, physical);

    /// <inheritdoc />
    public void ApplyKeyAndTableMapping<TEntity, TIdentifierType>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(typeof(TEntity).Name);
        builder.HasKey(p => p.Id);
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).ValueGeneratedOnAdd().UseIdentityColumn(1, 1);
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }

    /// <inheritdoc />
    /// <remarks>Keeps the SQL Server annotation it has always been given; the SQLite provider ignores it.</remarks>
    public IndexBuilder IncludeColumns(IndexBuilder index, params string[] propertyNames) =>
        SqlServerIndexBuilderExtensions.IncludeProperties(index, propertyNames);

    /// <inheritdoc />
    /// <remarks>SQL-standard double quotes, the same form <see cref="BuildSoftDeleteFilter"/> uses.</remarks>
    public string QuoteColumn(string column) => $"\"{column}\"";

    /// <inheritdoc />
    public string? BuildSoftDeleteFilter(string isDeletedColumn) => $"\"{isDeletedColumn}\" = 0";
}
