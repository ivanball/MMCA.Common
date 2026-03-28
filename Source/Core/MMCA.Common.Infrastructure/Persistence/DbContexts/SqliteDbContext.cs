using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting SQLite. Configured via connection string from <see cref="IConnectionStringSettings"/>.
/// Useful for lightweight local development or testing without a full SQL Server instance.
/// </summary>
public sealed class SqliteDbContext(
    DbContextOptions<SqliteDbContext> options,
    IServiceProvider serviceProvider,
    IConnectionStringSettings connectionStringSettings,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseSqlite(connectionStringSettings.SqliteConnectionString);

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.Sqlite, modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
