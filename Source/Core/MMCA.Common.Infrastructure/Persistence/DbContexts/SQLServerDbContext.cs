using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// DbContext targeting SQL Server. Configured via connection string from <see cref="IConnectionStringSettings"/>.
/// </summary>
public sealed class SQLServerDbContext(
    DbContextOptions<SQLServerDbContext> options,
    IServiceProvider serviceProvider,
    IConnectionStringSettings connectionStringSettings,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : ApplicationDbContext(options, serviceProvider, assemblyProvider)
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseSqlServer(
                connectionStringSettings.SQLServerConnectionString,
                sql =>
                {
                    if (!string.IsNullOrEmpty(connectionStringSettings.SQLServerMigrationsAssembly))
                    {
                        sql.MigrationsAssembly(connectionStringSettings.SQLServerMigrationsAssembly);
                    }
                });

        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.SQLServer, modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
