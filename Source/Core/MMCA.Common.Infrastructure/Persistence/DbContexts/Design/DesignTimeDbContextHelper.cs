using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Design;

/// <summary>
/// Builds <see cref="SQLServerDbContext"/> instances for <c>dotnet ef</c> design-time commands
/// without the application's DI container. A downstream migrations project implements EF's
/// <c>IDesignTimeDbContextFactory&lt;SQLServerDbContext&gt;</c> in a few lines:
/// <code language="csharp">
/// public sealed class ConferenceDbContextFactory : IDesignTimeDbContextFactory&lt;SQLServerDbContext&gt;
/// {
///     public SQLServerDbContext CreateDbContext(string[] args) =>
///         DesignTimeDbContextHelper.CreateSqlServer(args, options =>
///         {
///             options.ConnectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "...", SQLServerMigrationsAssembly = "..." };
///             options.AddConfigurationAssembly(typeof(ConferenceAssemblyReference).Assembly);
///         });
/// }
/// </code>
/// Invoked as <c>dotnet ef migrations add X --project ... -- --datasource Conference</c>
/// (EF forwards the arguments after <c>--</c> to the factory).
/// <para>
/// <see cref="CreateSqlite"/> is the symmetric entry point for a SQLite-backed application, which is
/// the same code path with <c>SqliteConnectionString</c> / <c>SqliteMigrationsAssembly</c> in place
/// of their SQL Server equivalents.
/// </para>
/// </summary>
public static class DesignTimeDbContextHelper
{
    /// <summary>
    /// Creates a <see cref="SQLServerDbContext"/> for the data source selected by
    /// <see cref="DesignTimeDbContextOptions.DataSourceName"/> or the <c>--datasource</c> argument.
    /// </summary>
    /// <param name="args">The design-time arguments forwarded by <c>dotnet ef</c> (after <c>--</c>).</param>
    /// <param name="configure">Callback configuring connection settings and configuration assemblies.</param>
    /// <returns>A context whose model contains only the selected data source's entities.</returns>
    public static SQLServerDbContext CreateSqlServer(string[] args, Action<DesignTimeDbContextOptions> configure)
    {
        var (services, assemblyProvider, physical) = BuildDesignTimeServices(DataSource.SQLServer, args, configure);

        return new SQLServerDbContext(
            new DbContextOptionsBuilder<SQLServerDbContext>().Options,
            services.BuildServiceProvider(),
            assemblyProvider,
            physical);
    }

    /// <summary>
    /// Creates a <see cref="SqliteDbContext"/> for the data source selected by
    /// <see cref="DesignTimeDbContextOptions.DataSourceName"/> or the <c>--datasource</c> argument,
    /// the SQLite counterpart of <see cref="CreateSqlServer"/>. A migrations project declares its
    /// connection through <c>SqliteConnectionString</c> and its own assembly through
    /// <c>SqliteMigrationsAssembly</c> on the matching <c>DataSources</c> entry.
    /// </summary>
    /// <param name="args">The design-time arguments forwarded by <c>dotnet ef</c> (after <c>--</c>).</param>
    /// <param name="configure">Callback configuring connection settings and configuration assemblies.</param>
    /// <returns>A context whose model contains only the selected data source's entities.</returns>
    public static SqliteDbContext CreateSqlite(string[] args, Action<DesignTimeDbContextOptions> configure)
    {
        var (services, assemblyProvider, physical) = BuildDesignTimeServices(DataSource.Sqlite, args, configure);

        return new SqliteDbContext(
            new DbContextOptionsBuilder<SqliteDbContext>().Options,
            services.BuildServiceProvider(),
            assemblyProvider,
            physical);
    }

    /// <summary>
    /// Builds the container, assembly provider and resolved physical source every design-time
    /// context needs. Shared by the per-engine factories so both scaffold against exactly the same
    /// interceptor and options pipeline: a difference between them would show up as a migration that
    /// differs by engine for reasons that have nothing to do with the engine.
    /// </summary>
    /// <param name="engine">The engine whose physical source is resolved.</param>
    /// <param name="args">The design-time arguments forwarded by <c>dotnet ef</c>.</param>
    /// <param name="configure">Callback configuring connection settings and configuration assemblies.</param>
    /// <returns>The populated service collection, the assembly provider, and the resolved source.</returns>
    private static (ServiceCollection Services, ExplicitAssemblyProvider AssemblyProvider, PhysicalDataSource Physical)
        BuildDesignTimeServices(DataSource engine, string[] args, Action<DesignTimeDbContextOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configure);

        var designOptions = new DesignTimeDbContextOptions();
        configure(designOptions);

        var logicalName = designOptions.DataSourceName
            ?? ParseDataSourceName(args)
            ?? DataSourceKey.DefaultName;

        var assemblyProvider = new ExplicitAssemblyProvider([.. designOptions.ConfigurationAssemblies]);
        var resolver = new DataSourceResolver(
            Options.Create(designOptions.ConnectionStrings),
            new DataSourcesSettings(designOptions.DataSources),
            NullLogger<DataSourceResolver>.Instance);
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        // Resolved before the registrations because the refresh-session gate below is keyed on the
        // PHYSICAL source name, which is not always the logical one asked for: a logical name whose
        // connection matches the top-level one collapses onto Default, and names sharing a
        // connection collapse onto the alphabetically-first of them.
        var physical = resolver.GetPhysical(resolver.ResolveLogical(engine, logicalName));

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IDomainEventDispatcher, NullDomainEventDispatcher>();
        services.AddSingleton<IOutboxSignal, OutboxSignal>();
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        // The tenant interceptor and the tenancy options are registered unconditionally (the
        // options defaulting to disabled), so `dotnet ef` builds exactly the runtime pipeline for
        // consumers with and without tenancy. Design time never resolves a tenant, so the
        // interceptor is inert and the Tenant query filter short-circuits: the scaffolded migration
        // is identical either way, apart from the TenantId column and index the model declares.
        services.AddSingleton<TenantSaveChangesInterceptor>();
        services.AddSingleton<IOptions<TenancySettings>>(Options.Create(new TenancySettings()));
        // The context reads Scheduler:Enabled from the root provider to decide whether the
        // ScheduledJobs table is part of the model. Registered unconditionally (defaulting to
        // disabled) so `dotnet ef` behaves identically for consumers with and without the flag.
        services.AddSingleton<IOptions<SchedulerSettings>>(
            Options.Create(new SchedulerSettings { Enabled = designOptions.EnableScheduler }));
        // Same treatment for the change-history table, and the interceptor that writes to it: the
        // context resolves the interceptor with GetService, so omitting it here would still work,
        // but registering it keeps the design-time pipeline identical to the runtime one.
        services.AddSingleton<IOptions<AuditTrailSettings>>(
            Options.Create(new AuditTrailSettings { Enabled = designOptions.EnableAuditTrail }));
        services.AddSingleton<AuditTrailSaveChangesInterceptor>();
        // Same treatment again for the refresh-session table, with one twist: its runtime gate is
        // two-part (enabled AND this context's source), so the registered source name is the one
        // THIS context resolved to. Registering the logical name instead would silently miss on
        // every collapse (a consumer whose Identity source shares the default connection resolves to
        // "Default"), and the scaffold would keep omitting a table the runtime model has.
        services.AddSingleton<IOptions<Application.Auth.RefreshSessionSettings>>(
            Options.Create(new Application.Auth.RefreshSessionSettings
            {
                Enabled = designOptions.EnableRefreshSessions,
                DataSourceName = physical.Key.Name,
            }));
        services.AddSingleton<IEntityConfigurationAssemblyProvider>(assemblyProvider);
        services.AddSingleton<IDataSourceResolver>(resolver);
        services.AddSingleton<IEntityDataSourceRegistry>(registry);

        return (services, assemblyProvider, physical);
    }

    /// <summary>
    /// Parses <c>--datasource &lt;Name&gt;</c> or <c>--datasource=Name</c> from the design-time arguments.
    /// </summary>
    internal static string? ParseDataSourceName(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--datasource", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length
                    ? args[i + 1]
                    : throw new InvalidOperationException("--datasource requires a value (e.g. -- --datasource Conference).");
            }

            if (args[i].StartsWith("--datasource=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i]["--datasource=".Length..];
            }
        }

        return null;
    }

    private sealed class ExplicitAssemblyProvider(IReadOnlyList<Assembly> assemblies) : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => assemblies;
    }

    private sealed class NullDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
