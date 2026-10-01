using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.LoadTests.Support;

/// <summary>
/// The framework's real persistence stack over one temp-file SQLite database, wired exactly the way a
/// host wires it: <c>AddApplication()</c> plus <c>AddInfrastructure(configuration)</c> from an
/// in-memory configuration, with this assembly registered for entity-configuration discovery. Nothing
/// below the service registrations is substituted; a scenario may only pre-register a service the
/// framework adds with <c>TryAdd</c> (the outbox scenario's counting <c>IMessageBus</c>).
/// </summary>
/// <remarks>
/// A FILE database rather than <c>:memory:</c>, because the scenarios open one connection per scope
/// (one per simulated request, and one per outbox cycle), as a deployed host does; an in-memory
/// database is private to the single connection that created it.
/// </remarks>
internal sealed class LoadStack : IAsyncDisposable
{
    private readonly string _databasePath;

    private LoadStack(string databasePath, ServiceProvider services)
    {
        _databasePath = databasePath;
        Services = services;
    }

    /// <summary>Gets the root provider; resolve scoped services through a scope, as a request would.</summary>
    public ServiceProvider Services { get; }

    /// <summary>
    /// Builds the stack and creates the schema (the OutboxMessages table plus every entity this
    /// assembly configures) through <see cref="IDbContextFactory.EnsureCreatedAsync"/>.
    /// </summary>
    /// <param name="name">A short scenario name, used in the temp file name.</param>
    /// <param name="preRegister">Registrations applied BEFORE the framework's, so they win over its TryAdd defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ready stack.</returns>
    public static async Task<LoadStack> CreateAsync(
        string name,
        Action<IServiceCollection>? preRegister,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mmca-load-{name}-{Guid.NewGuid():N}.db");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // Pooling off: with Microsoft.Data.Sqlite's pool on, about one full run in ten failed a
                // concurrent read with SQLite Error 5 "unable to delete/modify collation sequence due
                // to active statements" (2 of 22 local runs, 2026-10-01): EF re-registers its
                // collation on every open of a pooled handle. That is a provider-level defect of the
                // dev/test engine, not the framework read path this tier measures, so the tier opens
                // a fresh handle per connection rather than shipping a weekly red.
                ["ConnectionStrings:SqliteConnectionString"] = $"Data Source={databasePath};Pooling=False",

                // The outbox lives in the same SQLite database as the entities, and the processor is
                // registered: an in-process host leaves it off by default (MessageBusSettings).
                ["Outbox:DataSource"] = nameof(DataSource.Sqlite),
                ["MessageBus:EnableOutbox"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        preRegister?.Invoke(services);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddEntityConfigurationAssembly(typeof(LoadStack).Assembly);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var stack = new LoadStack(databasePath, provider);

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDbContextFactory>().EnsureCreatedAsync(cancellationToken);

        return stack;
    }

    /// <summary>
    /// Returns the context for the stack's one physical source, resolved the way the framework
    /// resolves it (logical Default name on the SQLite engine).
    /// </summary>
    /// <param name="scopedServices">The scope's service provider.</param>
    /// <returns>The scope's context for the SQLite database.</returns>
    public static ApplicationDbContext GetContext(IServiceProvider scopedServices)
    {
        var resolver = scopedServices.GetRequiredService<IDataSourceResolver>();
        var key = resolver.ResolveLogical(DataSource.Sqlite, DataSourceKey.DefaultName);
        return scopedServices.GetRequiredService<IDbContextFactory>().GetDbContext(key);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Best-effort temp file cleanup.
        }
    }
}
