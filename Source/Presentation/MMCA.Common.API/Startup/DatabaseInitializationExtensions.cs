using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Database initialization pipeline shared by all downstream MMCA applications.
/// Creates DbContexts, applies schema changes, and seeds data for enabled modules.
/// Operates per <b>physical data source</b>: every database in use by the host's registered
/// entities is initialized (migrated/created) independently.
/// </summary>
public static class DatabaseInitializationExtensions
{
    extension(IServiceProvider services)
    {
        /// <summary>
        /// Initializes databases by creating contexts, applying schema changes based on
        /// <see cref="ApplicationSettings.DatabaseInitStrategy"/>, and running module seeders.
        /// </summary>
        /// <param name="applicationSettings">Application settings containing the database init strategy.</param>
        /// <param name="moduleLoader">The module loader to seed enabled modules.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">
        /// <see cref="ApplicationSettings.DatabaseInitStrategy"/> is not <c>Migrate</c> or <c>None</c>.
        /// </exception>
        public async Task InitializeDatabaseAsync(
            ApplicationSettings applicationSettings,
            ModuleLoader moduleLoader,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(applicationSettings);
            ArgumentNullException.ThrowIfNull(moduleLoader);

            // Validated before anything is created: a misspelled strategy is a configuration mistake,
            // and the host must stop rather than reach the switch below having already created the
            // migrationless sources.
            EnsureKnownStrategy(applicationSettings.DatabaseInitStrategy);

            using var scope = services.CreateScope();

            // Warm the entity data-source registry: this scans configuration assemblies once and makes
            // entity-to-database routing deterministic before the first repository call (replacing the
            // legacy model-building side effect that populated the lazy DataSourceService cache).
            var registry = scope.ServiceProvider.GetRequiredService<IEntityDataSourceRegistry>();
            var resolver = scope.ServiceProvider.GetRequiredService<IDataSourceResolver>();
            var sourcesInUse = registry.GetPhysicalSourcesInUse();

            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();

            // Cosmos and SQLite sources are optional: integration tests may omit their connection
            // strings. A Cosmos source has no EF Core migrations pipeline at all, and neither does a
            // SQLite source with no migrations assembly configured, so both are created via
            // EnsureCreated up front, independent of the migration-oriented DatabaseInitStrategy
            // below. This is the ONLY path that creates them; without it such a source in use is
            // never created and the first repository call fails.
            //
            // A SQLite source WITH a migrations assembly is deliberately excluded here: EnsureCreated
            // writes the tables without an __EFMigrationsHistory row, after which every migration is
            // both pending and un-appliable (its CREATE TABLE hits an existing table).
            foreach (var migrationlessKey in sourcesInUse
                .Where(k => k.Engine is DataSource.CosmosDB or DataSource.Sqlite))
            {
                var physical = resolver.GetPhysical(migrationlessKey);

                if (string.IsNullOrEmpty(physical.ConnectionString))
                {
                    continue;
                }

                if (physical.UsesMigrations)
                {
                    continue;
                }

                await dbContextFactory.GetDbContext(migrationlessKey).Database
                    .EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }

            // Apply schema initialisation based on the configured strategy:
            //   "Migrate": auto-apply pending EF Core migrations per migrated source (development/testing).
            //   "None":    production, validate no pending migrations on any source, throw if behind.
            switch (applicationSettings.DatabaseInitStrategy)
            {
                case "Migrate":
                    await dbContextFactory.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "None":
                    await ThrowIfPendingMigrationsAsync(dbContextFactory, resolver, sourcesInUse, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw UnknownStrategy(applicationSettings.DatabaseInitStrategy);
            }

            await InitializeTenantDatabasesAsync(services, applicationSettings, resolver, sourcesInUse, cancellationToken)
                .ConfigureAwait(false);

            // Module seeding runs on the default scope only. A seeder writes reference data an
            // application needs to boot; running it per tenant would need a per-tenant notion of
            // "which seeders apply", which no module declares today, and running it twice against a
            // shared database is worse than not running it per tenant at all.
            await moduleLoader.SeedAllAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the same schema strategy to each tenant that keeps its own copy of a data source.
    /// Nothing else opens those databases, so without this pass a per-tenant database is never
    /// created and never migrated.
    /// </summary>
    /// <remarks>
    /// Each tenant gets a FRESH scope with its tenant set before the context factory is asked for
    /// anything: the scoped factory binds one physical database per source for the life of a scope,
    /// so reusing the outer scope would keep handing back the shared database.
    /// </remarks>
    private static async Task InitializeTenantDatabasesAsync(
        IServiceProvider services,
        ApplicationSettings applicationSettings,
        IDataSourceResolver resolver,
        IReadOnlyCollection<DataSourceKey> sourcesInUse,
        CancellationToken cancellationToken)
    {
        var settings = services.GetService<IOptions<TenancySettings>>()?.Value;
        if (settings is null || settings.Tenants.Count == 0)
        {
            return;
        }

        foreach (var target in TenantDataSourceTargets.Expand(sourcesInUse, settings)
            .Where(t => t.TenantId is not null))
        {
            using var tenantScope = services.CreateScope();
            tenantScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(target.TenantId!);

            var factory = tenantScope.ServiceProvider.GetRequiredService<IDbContextFactory>();
            var database = factory.GetDbContext(target.Source).Database;

            // The tenant's copy of a source is the same schema on a different connection, so it is
            // migrated exactly when the shared source is. The migrations assembly is declared once,
            // on the source, and a per-tenant override only replaces the connection string.
            var usesMigrations = resolver.GetPhysical(target.Source).UsesMigrations;

            switch (applicationSettings.DatabaseInitStrategy)
            {
                case "Migrate":
                    if (usesMigrations)
                    {
                        await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case "None":
                    await ThrowIfTenantPendingMigrationsAsync(database, target, usesMigrations, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw UnknownStrategy(applicationSettings.DatabaseInitStrategy);
            }
        }
    }

    /// <summary>
    /// Stops startup when <c>DatabaseInitStrategy</c> is anything other than <c>Migrate</c> or
    /// <c>None</c>. A value that is not recognised would otherwise leave the schema untouched and
    /// surface as a failing query long after startup reported success.
    /// </summary>
    /// <param name="strategy">The configured strategy.</param>
    /// <exception cref="InvalidOperationException">The strategy is not a valid value.</exception>
    private static void EnsureKnownStrategy(string? strategy)
    {
        if (!string.Equals(strategy, "Migrate", StringComparison.Ordinal)
            && !string.Equals(strategy, "None", StringComparison.Ordinal))
        {
            throw UnknownStrategy(strategy);
        }
    }

    /// <summary>The startup error for an unrecognised <c>DatabaseInitStrategy</c>, naming the valid values.</summary>
    /// <param name="strategy">The configured strategy.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException UnknownStrategy(string? strategy) =>
        new($"Unknown DatabaseInitStrategy: '{strategy}'. Valid values are: Migrate, None.");

    /// <summary>
    /// Production guard for a tenant database under the <c>"None"</c> strategy: a tenant left
    /// behind by a migration is exactly as broken as a shared database left behind, and silently so.
    /// </summary>
    private static async Task ThrowIfTenantPendingMigrationsAsync(
        DatabaseFacade database,
        TenantDataSourceTarget target,
        bool usesMigrations,
        CancellationToken cancellationToken)
    {
        if (!usesMigrations)
        {
            return;
        }

        var pending = await database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        if (!pending.Any())
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tenant database has pending migrations that must be applied before starting: "
            + $"{target}: {string.Join(", ", pending)}. "
            + "Run 'dotnet ef database update' against the tenant's connection string.");
    }

    /// <summary>
    /// Mirrors the context factory's own migration-target rule so the breakdown this file prints
    /// names exactly the sources the factory checked: a source a migrations pipeline owns, minus an
    /// optional non-SQL-Server source left without a connection string.
    /// </summary>
    /// <param name="physical">The resolved source.</param>
    /// <returns><see langword="true"/> when migrations are applied to this source.</returns>
    private static bool IsMigrationTarget(PhysicalDataSource physical) =>
        physical.UsesMigrations
        && (physical.Key.Engine == DataSource.SQLServer || !string.IsNullOrEmpty(physical.ConnectionString));

    /// <summary>
    /// Production guard for the <c>"None"</c> strategy: throws with a per-source breakdown when
    /// any migrated data source in use has migrations that have not been applied. The breakdown
    /// covers exactly the sources the factory migrates, so a SQLite source with a migrations
    /// assembly is named here rather than reported as an empty list.
    /// </summary>
    private static async Task ThrowIfPendingMigrationsAsync(
        IDbContextFactory dbContextFactory,
        IDataSourceResolver resolver,
        IReadOnlyCollection<DataSourceKey> sourcesInUse,
        CancellationToken cancellationToken)
    {
        if (!await dbContextFactory.HasPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var pendingPerSource = new List<string>();
        foreach (var migratedKey in sourcesInUse.Where(k => IsMigrationTarget(resolver.GetPhysical(k))))
        {
            var pending = await dbContextFactory.GetDbContext(migratedKey).Database
                .GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
            {
                pendingPerSource.Add($"{migratedKey}: {string.Join(", ", pending)}");
            }
        }

        throw new InvalidOperationException(
            $"Database has pending migrations that must be applied before starting: {string.Join("; ", pendingPerSource)}. " +
            "Run 'dotnet ef database update' or apply the migration SQL script.");
    }
}
