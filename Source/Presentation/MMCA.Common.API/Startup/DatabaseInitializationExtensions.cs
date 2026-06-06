using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Database initialization pipeline shared by all downstream MMCA applications.
/// Creates DbContexts, applies schema changes, and seeds data for enabled modules.
/// Operates per <b>physical data source</b>: every database in use by the host's registered
/// entities is initialized (migrated/created) independently.
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

        // Warm the entity data-source registry: this scans configuration assemblies once and makes
        // entity-to-database routing deterministic before the first repository call (replacing the
        // legacy model-building side effect that populated the lazy DataSourceService cache).
        var registry = scope.ServiceProvider.GetRequiredService<IEntityDataSourceRegistry>();
        var resolver = scope.ServiceProvider.GetRequiredService<IDataSourceResolver>();
        var sourcesInUse = registry.GetPhysicalSourcesInUse();

        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();

        // Cosmos sources are optional — integration tests may omit their connection strings.
        // Cosmos is a document DB with no schema migrations; always use EnsureCreated.
        foreach (var cosmosKey in sourcesInUse.Where(k => k.Engine == DataSource.CosmosDB))
        {
            if (string.IsNullOrEmpty(resolver.GetPhysical(cosmosKey).ConnectionString))
            {
                continue;
            }

            await dbContextFactory.GetDbContext(cosmosKey).Database
                .EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Apply schema initialisation based on the configured strategy:
        //   "Migrate"       — auto-apply pending EF Core migrations per SQL Server source (development/testing).
        //   "EnsureCreated" — legacy EnsureCreated behavior for every source in use.
        //   "None"          — production: validate no pending migrations on any source, throw if behind.
        switch (applicationSettings.DatabaseInitStrategy)
        {
            case "Migrate":
                await dbContextFactory.MigrateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "EnsureCreated":
                await dbContextFactory.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "None":
                await ThrowIfPendingMigrationsAsync(dbContextFactory, sourcesInUse, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown DatabaseInitStrategy: '{applicationSettings.DatabaseInitStrategy}'. " +
                    "Valid values are: Migrate, EnsureCreated, None.");
        }

        await moduleLoader.SeedAllAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Production guard for the <c>"None"</c> strategy: throws with a per-source breakdown when
    /// any SQL Server data source in use has migrations that have not been applied.
    /// </summary>
    private static async Task ThrowIfPendingMigrationsAsync(
        IDbContextFactory dbContextFactory,
        IReadOnlyCollection<DataSourceKey> sourcesInUse,
        CancellationToken cancellationToken)
    {
        if (!await dbContextFactory.HasPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var pendingPerSource = new List<string>();
        foreach (var sqlKey in sourcesInUse.Where(k => k.Engine == DataSource.SQLServer))
        {
            var pending = await dbContextFactory.GetDbContext(sqlKey).Database
                .GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
            {
                pendingPerSource.Add($"{sqlKey}: {string.Join(", ", pending)}");
            }
        }

        throw new InvalidOperationException(
            $"Database has pending migrations that must be applied before starting: {string.Join("; ", pendingPerSource)}. " +
            "Run 'dotnet ef database update' or apply the migration SQL script.");
    }
}
