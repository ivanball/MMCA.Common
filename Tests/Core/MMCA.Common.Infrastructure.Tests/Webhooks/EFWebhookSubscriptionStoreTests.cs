using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Infrastructure.Webhooks;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>EFWebhookSubscriptionStore</c> (design 35.12, 35.13, 35.17 and 35.34) on SQLite in memory, no
/// server: the store <c>AddWebhooks</c> registers, resolved over the real <see cref="DbContextFactory"/>
/// and a context whose model the real gate builds (<c>Webhooks:Enabled</c>, source <c>Default</c>).
/// Every read filters by tenant with null matching only null, the enabled list drops disabled and
/// soft-deleted rows, and recording an outcome swallows a lost concurrency race. Each
/// <see cref="StoreHandle"/> is one scope with its own context over the one shared connection, so two
/// handles stand for two concurrent requests.
/// </summary>
public sealed class EFWebhookSubscriptionStoreTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    private static readonly DateTime Now = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _contextServices;

    public EFWebhookSubscriptionStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _contextServices = BuildContextServices();

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contextServices.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task TenantNullMatchesOnlyNull()
    {
        var untenanted = NewSubscription(tenantId: null);
        var acme = NewSubscription(Acme);
        var globex = NewSubscription(Globex);
        using (var seed = OpenStore())
        {
            await seed.Store.AddAsync(untenanted, Ct);
            await seed.Store.AddAsync(acme, Ct);
            await seed.Store.AddAsync(globex, Ct);
        }

        using var reader = OpenStore();

        (await reader.Store.ListAsync(null, Ct)).Should().ContainSingle(
            "a null tenant matches only the null-tenant rows, never every row").Which.Id.Should().Be(untenanted.Id);
        (await reader.Store.ListAsync(Acme, Ct)).Select(s => s.Id).Should().Equal(acme.Id);
        (await reader.Store.ListEnabledAsync(null, Ct)).Select(s => s.Id).Should().Equal(untenanted.Id);
        (await reader.Store.ListEnabledAsync(Globex, Ct)).Select(s => s.Id).Should().Equal(globex.Id);
        (await reader.Store.CountAsync(null, Ct)).Should().Be(1);
        (await reader.Store.CountAsync(Acme, Ct)).Should().Be(1);
        (await reader.Store.FindAsync(acme.Id, null, Ct)).Should().BeNull("an untenanted caller cannot read a tenant's row");
        (await reader.Store.FindAsync(untenanted.Id, Acme, Ct)).Should().BeNull("a tenant cannot read an untenanted row");
        (await reader.Store.FindAsync(acme.Id, Globex, Ct)).Should().BeNull("a tenant cannot read another tenant's row");
        (await reader.Store.FindAsync(acme.Id, Acme, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabledAndDeleted()
    {
        var enabled = NewSubscription(Acme);
        var disabled = NewSubscription(Acme);
        var deleted = NewSubscription(Acme);
        using (var seed = OpenStore())
        {
            await seed.Store.AddAsync(enabled, Ct);
            await seed.Store.AddAsync(disabled, Ct);
            await seed.Store.AddAsync(deleted, Ct);

            var toDisable = (await seed.Store.FindAsync(disabled.Id, Acme, Ct))!;
            toDisable.Disable(Now);
            await seed.Store.SaveAsync(toDisable, Ct);

            var toDelete = (await seed.Store.FindAsync(deleted.Id, Acme, Ct))!;
            toDelete.Delete().IsSuccess.Should().BeTrue();
            await seed.Store.SaveAsync(toDelete, Ct);
        }

        using var reader = OpenStore();

        (await reader.Store.ListEnabledAsync(Acme, Ct)).Select(s => s.Id).Should().Equal(enabled.Id);
        Guid[] listed = [enabled.Id, disabled.Id];
        (await reader.Store.ListAsync(Acme, Ct)).Select(s => s.Id).Should().BeEquivalentTo(
            listed, "the admin list shows a disabled subscription but never a deleted one");
        (await reader.Store.CountAsync(Acme, Ct)).Should().Be(2, "the per-tenant limit counts non-deleted rows (rule 35.13)");
        (await reader.Store.FindAsync(deleted.Id, Acme, Ct)).Should().BeNull("a deleted id answers NotFound (rule 35.17)");
    }

    [Fact]
    public async Task RecordOutcome_SwallowsConcurrencyConflict()
    {
        var subscription = NewSubscription(Acme);
        using (var seed = OpenStore())
        {
            await seed.Store.AddAsync(subscription, Ct);
        }

        using var delivery = OpenStore();
        using var admin = OpenStore();
        var deliveredCopy = (await delivery.Store.FindAsync(subscription.Id, Acme, Ct))!;
        var adminCopy = (await admin.Store.FindAsync(subscription.Id, Acme, Ct))!;

        // The administrator saves first, which moves the row version under the delivery's copy.
        adminCopy.Disable(Now);
        await admin.Store.SaveAsync(adminCopy, Ct);

        deliveredCopy.RecordDelivered(Now.AddSeconds(1));
        var act = () => delivery.Store.RecordOutcomeAsync(deliveredCopy, Ct);

        await act.Should().NotThrowAsync("a lost race on the last-outcome columns must not fail a delivery that already happened");
        using var reader = OpenStore();
        var stored = (await reader.Store.FindAsync(subscription.Id, Acme, Ct))!;
        stored.DisabledOn.Should().Be(Now, "the winning write is kept");
    }

    [Fact]
    public async Task RecordOutcome_PersistsTheLastOutcome()
    {
        var subscription = NewSubscription(Acme);
        using (var seed = OpenStore())
        {
            await seed.Store.AddAsync(subscription, Ct);
        }

        using (var delivery = OpenStore())
        {
            var tracked = (await delivery.Store.FindAsync(subscription.Id, Acme, Ct))!;
            tracked.RecordFailed(Now, "HTTP 500");
            await delivery.Store.RecordOutcomeAsync(tracked, Ct);
        }

        using var reader = OpenStore();
        var stored = (await reader.Store.FindAsync(subscription.Id, Acme, Ct))!;
        stored.LastFailedOn.Should().Be(Now);
        stored.LastFailure.Should().Be("HTTP 500");
    }

    [Fact]
    public async Task Save_PersistsAnUpdate()
    {
        var subscription = NewSubscription(Acme);
        using (var seed = OpenStore())
        {
            await seed.Store.AddAsync(subscription, Ct);
        }

        using (var admin = OpenStore())
        {
            var tracked = (await admin.Store.FindAsync(subscription.Id, Acme, Ct))!;
            tracked.Update("https://hooks.example.com/v2", "renamed", ["ticket.closed", "ticket.opened"]).IsSuccess.Should().BeTrue();
            await admin.Store.SaveAsync(tracked, Ct);
        }

        using var reader = OpenStore();
        var stored = (await reader.Store.FindAsync(subscription.Id, Acme, Ct))!;
        stored.Url.Should().Be("https://hooks.example.com/v2");
        stored.Description.Should().Be("renamed");
        stored.GetEventTypes().Should().Equal("ticket.closed", "ticket.opened");
        stored.ProtectedSecret.Should().Be("protected-secret");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebhookSubscription NewSubscription(string? tenantId) =>
        WebhookSubscription.Create("https://hooks.example.com/in", "test", ["ticket.opened"], tenantId, "protected-secret").Value!;

    private static ServiceProvider BuildContextServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton(Mock.Of<IDomainEventDispatcher>());
        services.AddSingleton(Mock.Of<IOutboxSignal>());
        services.AddSingleton<ILogger<DomainEventSaveChangesInterceptor>>(NullLogger<DomainEventSaveChangesInterceptor>.Instance);
        services.AddSingleton<DomainEventSaveChangesInterceptor>();
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

        // The gate reads this: Enabled, and the context's source name (Default) is the configured one.
        services.AddSingleton<IOptions<WebhookSettings>>(Options.Create(new WebhookSettings { Enabled = true }));
        return services.BuildServiceProvider();
    }

    private StoreTestContext NewContext() => new(_connection, _contextServices);

    private StoreHandle OpenStore()
    {
        var context = NewContext();
        var physicalFactory = new Mock<IPhysicalDbContextFactory>();
        physicalFactory.Setup(f => f.Create(It.IsAny<DataSourceKey>())).Returns(context);
        var registry = new EmptyEntityDataSourceRegistry();
        var resolver = new DefaultDataSourceResolver();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Webhooks:Enabled"] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEntityDataSourceRegistry>(registry);
        services.AddSingleton<IDataSourceResolver>(resolver);
        services.AddSingleton<IConcurrencyConflictDetector, EfCoreConcurrencyConflictDetector>();
        services.AddScoped<IDbContextFactory>(_ => new DbContextFactory(
            physicalFactory.Object,
            registry,
            resolver,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<ITenantContext>(),
            Options.Create(new TenancySettings())));
        services.AddWebhooks(configuration);

        var provider = services.BuildServiceProvider();
        return new StoreHandle(provider, provider.CreateScope(), context);
    }

    /// <summary>One request's scope: its own store, factory and context.</summary>
    private sealed class StoreHandle : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly StoreTestContext _context;

        public StoreHandle(ServiceProvider provider, IServiceScope scope, StoreTestContext context)
        {
            _provider = provider;
            _scope = scope;
            _context = context;
            Store = scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionStore>();
        }

        public IWebhookSubscriptionStore Store { get; }

        public void Dispose()
        {
            _scope.Dispose();
            _context.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>
    /// A SQLite context of its own type (so no other test's cached model is reused) that keeps the
    /// base model building, which is where the webhook gate runs.
    /// </summary>
    private sealed class StoreTestContext(SqliteConnection connection, IServiceProvider serviceProvider)
        : ApplicationDbContext(
            new DbContextOptionsBuilder<StoreTestContext>().Options,
            serviceProvider,
            new NoConfigurationAssemblies(),
            TestPhysicalDataSources.Sqlite())
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            base.OnConfiguring(optionsBuilder);
        }
    }

    private sealed class NoConfigurationAssemblies : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
