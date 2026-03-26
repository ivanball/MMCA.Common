using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Database initialization pipeline shared by all downstream MMCA applications.
/// Creates DbContexts, applies schema changes, and seeds data for enabled modules.
/// </summary>
public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Initializes databases by creating contexts, applying schema changes based on
    /// <see cref="ApplicationSettings.DatabaseInitStrategy"/>, and running module seeders.
    /// </summary>
    /// <param name="services">The service provider (typically from <c>app.Services</c>).</param>
    /// <param name="applicationSettings">Application settings containing the database init strategy.</param>
    /// <param name="moduleLoader">The module loader to seed enabled modules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        ApplicationSettings applicationSettings,
        ModuleLoader moduleLoader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(applicationSettings);
        ArgumentNullException.ThrowIfNull(moduleLoader);

        using var scope = services.CreateScope();

        // Eagerly resolve each DbContext to populate the DataSourceService cache;
        // subsequent repository calls can then retrieve the correct context by DataSource key.
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
        _ = (SQLServerDbContext)dbContextFactory.GetDbContext(DataSource.SQLServer);

        // Cosmos is optional — integration tests may omit its connection string.
        // Cosmos is a document DB with no schema migrations; always use EnsureCreated.
        var connectionStrings = scope.ServiceProvider.GetRequiredService<IConnectionStringSettings>();
        if (!string.IsNullOrEmpty(connectionStrings.CosmosConnectionString))
        {
            var cosmosContext = (CosmosDbContext)dbContextFactory.GetDbContext(DataSource.CosmosDB);
            await cosmosContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Apply schema initialisation based on the configured strategy:
        //   "Migrate"       — auto-apply pending EF Core migrations (development/testing).
        //   "EnsureCreated" — legacy EnsureCreated behavior.
        //   "None"          — production: validate no pending migrations, throw if behind.
        switch (applicationSettings.DatabaseInitStrategy)
        {
            case "Migrate":
                await dbContextFactory.MigrateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "EnsureCreated":
                await dbContextFactory.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "None":
                if (await dbContextFactory.HasPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
                {
                    var pending = await dbContextFactory.GetDbContext(DataSource.SQLServer)
                        .Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Database has pending migrations that must be applied before starting: {string.Join(", ", pending)}. " +
                        "Run 'dotnet ef database update' or apply the migration SQL script.");
                }

                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown DatabaseInitStrategy: '{applicationSettings.DatabaseInitStrategy}'. " +
                    "Valid values are: Migrate, EnsureCreated, None.");
        }

        await moduleLoader.SeedAllAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }
}
