using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;

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
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configure);

        var designOptions = new DesignTimeDbContextOptions();
        configure(designOptions);

        var logicalName = designOptions.DataSourceName
            ?? ParseDataSourceName(args)
            ?? DataSourceKey.DefaultName;

        var assemblyProvider = new ExplicitAssemblyProvider([.. designOptions.ConfigurationAssemblies]);
        var resolver = new DataSourceResolver(
            designOptions.ConnectionStrings,
            new DataSourcesSettings(designOptions.DataSources),
            NullLogger<DataSourceResolver>.Instance);
        var registry = new EntityDataSourceRegistry(assemblyProvider, resolver);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IDomainEventDispatcher, NullDomainEventDispatcher>();
        services.AddSingleton<IOutboxSignal, OutboxSignal>();
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        services.AddSingleton<IEntityConfigurationAssemblyProvider>(assemblyProvider);
        services.AddSingleton<IDataSourceResolver>(resolver);
        services.AddSingleton<IEntityDataSourceRegistry>(registry);

        var physical = resolver.GetPhysical(resolver.ResolveLogical(DataSource.SQLServer, logicalName));

        return new SQLServerDbContext(
            new DbContextOptionsBuilder<SQLServerDbContext>().Options,
            services.BuildServiceProvider(),
            assemblyProvider,
            physical);
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
