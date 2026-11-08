using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.FeatureFlags;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.FeatureFlags;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.FeatureFlags;

/// <summary>
/// The flags these tests declare, the way a module's <c>*.Shared</c> assembly declares its own
/// (ADR-031). <see cref="ReloadableFlagsHost"/> passes THIS assembly to
/// <c>AddReloadableFeatureFlags</c>, so these are the only names an override may be written for.
/// </summary>
public static class ReloadableTestFeatures
{
    /// <summary>Configured <see langword="true"/>.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "framework-tests")]
    public const string ConfiguredOn = "Reloadable.ConfiguredOn";

    /// <summary>Configured <see langword="false"/>.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "framework-tests")]
    public const string ConfiguredOff = "Reloadable.ConfiguredOff";

    /// <summary>Declared but absent from configuration.</summary>
    [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "2099-01-01", Owner = "framework-tests")]
    public const string NotConfigured = "Reloadable.NotConfigured";
}

/// <summary>
/// A host built the way a service opts in: configuration-backed feature management, the two-level
/// cache, and <c>AddReloadableFeatureFlags</c> over a real SQLite database whose model is built by
/// <see cref="ApplicationDbContext"/> itself (so the table gate, the soft-delete filter and the audit
/// stamps are the production ones). Each DI scope gets its own context over one shared connection,
/// the way each request does.
/// </summary>
internal sealed class ReloadableFlagsHost : IAsyncDisposable
{
    /// <summary>A name configuration knows (as <see langword="true"/>) that no <c>*Features</c> class declares.</summary>
    public const string ConfiguredButUndeclared = "Reloadable.ConfiguredButUndeclared";

    /// <summary>The user every save in this host is stamped with.</summary>
    public const int OperatorId = 7;

    private readonly SqliteConnection _connection;
    private readonly ConcurrentBag<ApplicationDbContext> _contexts = [];
    private ServiceProvider _provider = null!;

    private ReloadableFlagsHost(SqliteConnection connection) => _connection = connection;

    /// <summary>The targeting context the targeting filter evaluates against.</summary>
    public MutableTargetingContextAccessor Targeting =>
        (MutableTargetingContextAccessor)_provider.GetRequiredService<ITargetingContextAccessor>();

    /// <summary>The root provider (the feature manager is a singleton).</summary>
    public IServiceProvider Services => _provider;

    /// <summary>The feature manager every FeatureGate decorator reads.</summary>
    public IFeatureManager FeatureManager => _provider.GetRequiredService<IFeatureManager>();

    public static async Task<ReloadableFlagsHost> CreateAsync(int overrideCacheSeconds = 30)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        var host = new ReloadableFlagsHost(connection);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"FeatureManagement:{ReloadableTestFeatures.ConfiguredOn}"] = "true",
                [$"FeatureManagement:{ReloadableTestFeatures.ConfiguredOff}"] = "false",
                [$"FeatureManagement:{ConfiguredButUndeclared}"] = "true",
                ["FeatureFlags:OverrideCacheSeconds"] = overrideCacheSeconds.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddFeatureManagement().WithTargeting<MutableTargetingContextAccessor>();
        services.AddCommonHybridCache();
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        services.AddSingleton<IDataSourceResolver>(new DefaultDataSourceResolver());
        services.AddScoped(_ => host.NewFactory());

        services.AddReloadableFeatureFlags(configuration, typeof(ReloadableTestFeatures).Assembly);

        host._provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return host;
    }

    /// <summary>Runs one administration call in its own scope, the way one request would.</summary>
    /// <typeparam name="T">The call's result.</typeparam>
    public async Task<T> AdminAsync<T>(Func<IFeatureFlagAdministrationService, Task<T>> call)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<IFeatureFlagAdministrationService>());
    }

    /// <summary>
    /// Writes an override row straight into the database, behind the administration service's back,
    /// so nothing invalidates the cached snapshot.
    /// </summary>
    public async Task InsertBehindTheCachesBackAsync(string name, FeatureFlagOverrideMode mode)
    {
        await using var context = FlagTestContext.Create(_connection);
        context.Add(FeatureFlagOverride.Create(name, mode, "Written directly to the table").Value!);
        await context.SaveChangesAsync(OperatorId, CancellationToken.None);
    }

    /// <summary>Reads every row, cleared ones included, with nothing filtered.</summary>
    public async Task<List<FeatureFlagOverride>> ReadAllRowsAsync()
    {
        await using var context = FlagTestContext.Create(_connection);
        return await context.Set<FeatureFlagOverride>().IgnoreQueryFilters().AsNoTracking()
            .ToListAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private IDbContextFactory NewFactory()
    {
        var context = FlagTestContext.Create(_connection);
        _contexts.Add(context);

        var factory = new Mock<IDbContextFactory>();
        factory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(context);
        factory
            .Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => context.SaveChangesAsync(OperatorId, ct));
        return factory.Object;
    }

    /// <summary>A targeting context a test can point at one user and their groups.</summary>
    internal sealed class MutableTargetingContextAccessor : ITargetingContextAccessor
    {
        public TargetingContext Current { get; set; } = new() { UserId = "anonymous", Groups = [] };

        public ValueTask<TargetingContext> GetContextAsync() => ValueTask.FromResult(Current);
    }

    /// <summary>
    /// The production base context over SQLite, opted in exactly as <c>AddReloadableFeatureFlags</c>
    /// opts a host in: the model gate is registered and the settings name this context's source.
    /// </summary>
    private sealed class FlagTestContext : ApplicationDbContext
    {
        private static readonly IServiceProvider ContextServices = BuildContextServices();

        private FlagTestContext(DbContextOptions<FlagTestContext> options)
            : base(options, ContextServices, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static FlagTestContext Create(SqliteConnection connection)
        {
            var context = new FlagTestContext(new DbContextOptionsBuilder<FlagTestContext>().UseSqlite(connection).Options);
            context.Database.EnsureCreated();
            return context;
        }

        private static ServiceProvider BuildContextServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                Mock.Of<ILogger<DomainEventSaveChangesInterceptor>>(),
                Mock.Of<IOutboxSignal>(),
                timeProvider: TimeProvider.System));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            services.AddSingleton<FeatureFlagOverrideModelGate>();
            services.AddSingleton<IOptions<FeatureFlagOverrideSettings>>(Options.Create(new FeatureFlagOverrideSettings()));
            return services.BuildServiceProvider();
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
