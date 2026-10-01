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
/// PostgreSQL (Npgsql). Takes the SQL Server mapping unchanged (module schema, PascalCase), but
/// double-quotes identifiers, compares the soft-delete flag with <see langword="false"/>, stamps its own row
/// version, and sorts nulls last ascending.
/// </summary>
internal sealed class PostgreSQLDataSourceEngine : IDataSourceEngine
{
    // Empty options: all configuration (provider, connection, interceptors) happens in OnConfiguring.
    private static readonly DbContextOptions<PostgreSQLDbContext> Options =
        new DbContextOptionsBuilder<PostgreSQLDbContext>().Options;

    /// <inheritdoc />
    public DataSource Engine => DataSource.PostgreSQL;

    /// <inheritdoc />
    public int SubstitutionPriority => 1;

    /// <inheritdoc />
    public Type EntityConfigurationInterface => typeof(IEntityTypeConfigurationPostgreSQL<,>);

    /// <inheritdoc />
    public string? MigrationsAssemblySettingName => nameof(DataSourceEntrySettings.PostgreSQLMigrationsAssembly);

    /// <inheritdoc />
    public bool ConnectionIdentityIncludesDatabaseName => false;

    /// <inheritdoc />
    public DataSourceEngineCapabilities Capabilities { get; } = new(
        Migrations: MigrationPolicy.WhenAssemblyConfigured,
        ConnectionStringRequired: false,
        IsRelational: true,
        NullsSortFirstAscending: false,
        RowVersion: RowVersionStrategy.ClientStamped);

    /// <inheritdoc />
    public IExplicitKeyInsertDialect? ExplicitKeyInsert => null;

    /// <inheritdoc />
    public string GetConnectionString(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.PostgreSQLConnectionString;
    }

    /// <inheritdoc />
    public string GetConnectionString(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.PostgreSQLConnectionString;
    }

    /// <inheritdoc />
    public string? GetConnectionString(TenantDataSourceOverrideSettings tenantOverride)
    {
        ArgumentNullException.ThrowIfNull(tenantOverride);
        return tenantOverride.PostgreSQLConnectionString;
    }

    /// <inheritdoc />
    public string GetTopLevelMigrationsAssembly(ConnectionStringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.PostgreSQLMigrationsAssembly;
    }

    /// <inheritdoc />
    public string GetMigrationsAssembly(DataSourceEntrySettings entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.PostgreSQLMigrationsAssembly;
    }

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider,
        PhysicalDataSource physical) =>
        new PostgreSQLDbContext(Options, serviceProvider, assemblyProvider, physical);

    /// <inheritdoc />
    public void ApplyKeyAndTableMapping<TEntity, TIdentifierType>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Deliberately identical to SQL Server (ADR-113): an entity moves between the two engines by
        // changing its configuration base class and nothing else.
        builder.ToTable(typeof(TEntity).Name, NamespaceConventions.GetModuleName(typeof(TEntity)) ?? "dbo");
        builder.HasKey(p => p.Id);
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).ValueGeneratedOnAdd();
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }

    /// <inheritdoc />
    public IndexBuilder IncludeColumns(IndexBuilder index, params string[] propertyNames) =>
        NpgsqlIndexBuilderExtensions.IncludeProperties(index, propertyNames);

    /// <inheritdoc />
    public string QuoteColumn(string column) => $"\"{column}\"";

    /// <inheritdoc />
    public string? BuildSoftDeleteFilter(string isDeletedColumn) => $"\"{isDeletedColumn}\" = false";
}
