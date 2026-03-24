using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
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
    TimeProvider timeProvider,
    IConnectionStringSettings connectionStringSettings,
    ILogger<ApplicationDbContext> logger,
    IDomainEventDispatcher domainEventDispatcher,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : ApplicationDbContext(options, serviceProvider, timeProvider, logger, domainEventDispatcher, assemblyProvider)
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder
            .UseSqlServer(connectionStringSettings.SQLServerConnectionString);

        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsForEntitiesInContext(DataSource.SQLServer, modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
