using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// End-to-end database-per-microservice tests over two real SQLite databases: entity routing via
/// the unit of work, per-source outbox capture, same-connection collapse to one context, and
/// cross-source navigation population through <see cref="NavigationLoader"/>.
/// </summary>
public sealed class MultiSourceSqliteIntegrationTests : IDisposable
{
    private readonly string _databaseAPath;
    private readonly string _databaseBPath;
    private readonly ServiceProvider _serviceProvider;
    private readonly DataSourceResolver _resolver;
    private readonly EntityDataSourceRegistry _registry;
    private readonly DbContextFactory _dbContextFactory;
    private readonly UnitOfWork _unitOfWork;

    public MultiSourceSqliteIntegrationTests()
    {
        _databaseAPath = Path.Combine(Path.GetTempPath(), $"mmca-multisource-a-{Guid.NewGuid():N}.db");
        _databaseBPath = Path.Combine(Path.GetTempPath(), $"mmca-multisource-b-{Guid.NewGuid():N}.db");

        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["SourceA"] = new() { SqliteConnectionString = $"Data Source={_databaseAPath}" },
            ["SourceB"] = new() { SqliteConnectionString = $"Data Source={_databaseBPath}" },
        });

        _resolver = new DataSourceResolver(connectionStrings, dataSources, NullLogger<DataSourceResolver>.Instance);

        var assemblyProvider = new FixedAssemblyProvider();
        _registry = new EntityDataSourceRegistry(assemblyProvider, _resolver);

        _serviceProvider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(_registry)
            .AddSingleton<IDataSourceResolver>(_resolver)
            .BuildServiceProvider();

        var physicalFactory = new PhysicalDbContextFactory(_serviceProvider, _resolver, assemblyProvider);
        _dbContextFactory = new DbContextFactory(physicalFactory, _registry, _resolver, Mock.Of<ICurrentUserService>());

        var applicationSettings = Mock.Of<IApplicationSettings>();
        _unitOfWork = new UnitOfWork(
            _dbContextFactory,
            new DataSourceService(_registry),
            new RepositoryFactory(_serviceProvider, applicationSettings));

        // Create both schemas (Default(Sqlite) has no connection string and is skipped).
        CreateSchema("SourceA");
        CreateSchema("SourceB");
    }

    public void Dispose()
    {
        _unitOfWork.Dispose();
        _dbContextFactory.Dispose();
        _serviceProvider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_databaseAPath);
        TryDelete(_databaseBPath);
    }

    // ── Routing: each entity lands in its own database ──
    [Fact]
    public async Task UnitOfWork_RoutesEachEntityToItsOwnDatabase()
    {
        var orderRepository = _unitOfWork.GetRepository<MultiSourceOrder, int>();
        var customerRepository = _unitOfWork.GetRepository<MultiSourceCustomer, int>();

        await orderRepository.AddAsync(new MultiSourceOrder { Id = 1, Name = "Order-1", CustomerId = 10 });
        await customerRepository.AddAsync(new MultiSourceCustomer { Id = 10, Name = "Customer-10" });
        await _unitOfWork.SaveChangesAsync();

        // Each entity exists only in its own database.
        var contextA = _dbContextFactory.GetDbContext(_resolver.ResolveLogical(DataSource.Sqlite, "SourceA"));
        var contextB = _dbContextFactory.GetDbContext(_resolver.ResolveLogical(DataSource.Sqlite, "SourceB"));
        ReferenceEquals(contextA, contextB).Should().BeFalse();

        (await contextA.Set<MultiSourceOrder>().CountAsync()).Should().Be(1);
        (await contextB.Set<MultiSourceCustomer>().CountAsync()).Should().Be(1);

        contextA.Model.FindEntityType(typeof(MultiSourceCustomer)).Should().BeNull("Customer belongs to SourceB");
        contextB.Model.FindEntityType(typeof(MultiSourceOrder)).Should().BeNull("Order belongs to SourceA");
    }

    // ── Outbox: domain events land in the same database as the aggregate ──
    [Fact]
    public async Task DomainEvent_IsCapturedInTheAggregatesOwnOutbox()
    {
        var order = new MultiSourceOrder { Id = 2, Name = "Order-2", CustomerId = 11 };
        order.AddDomainEvent(new MultiSourceTestEvent());

        var orderRepository = _unitOfWork.GetRepository<MultiSourceOrder, int>();
        await orderRepository.AddAsync(order);
        await _unitOfWork.SaveChangesAsync();

        var contextA = _dbContextFactory.GetDbContext(_resolver.ResolveLogical(DataSource.Sqlite, "SourceA"));
        var contextB = _dbContextFactory.GetDbContext(_resolver.ResolveLogical(DataSource.Sqlite, "SourceB"));

        (await contextA.Set<OutboxMessage>().CountAsync()).Should().Be(1, "the event's aggregate lives in SourceA");
        (await contextB.Set<OutboxMessage>().CountAsync()).Should().Be(0, "SourceB had no changes");
    }

    // ── Cross-source navigation population (parent in A, child in B) ──
    [Fact]
    public async Task NavigationLoader_PopulatesCrossSourceReference()
    {
        var customerRepository = _unitOfWork.GetRepository<MultiSourceCustomer, int>();
        await customerRepository.AddAsync(new MultiSourceCustomer { Id = 20, Name = "Customer-20" });
        await customerRepository.AddAsync(new MultiSourceCustomer { Id = 21, Name = "Customer-21" });

        var orderRepository = _unitOfWork.GetRepository<MultiSourceOrder, int>();
        await orderRepository.AddAsync(new MultiSourceOrder { Id = 3, Name = "Order-3", CustomerId = 20 });
        await orderRepository.AddAsync(new MultiSourceOrder { Id = 4, Name = "Order-4", CustomerId = 21 });
        await _unitOfWork.SaveChangesAsync();

        var orders = await orderRepository.GetAllAsync([]);

        // EF cannot Include across databases — the Customer navigation is not even part of
        // SourceA's model. NavigationLoader batch-loads it through SourceB's repository instead.
        await NavigationLoader.LoadFKPropertyAsync(
            orders,
            order => order.CustomerId,
            customer => customer.Id,
            _unitOfWork.GetReadRepository<MultiSourceCustomer, int>(),
            (order, customers) => order.Customer = customers.FirstOrDefault(),
            CancellationToken.None);

        orders.Should().HaveCount(2);
        orders.Single(o => o.Id == 3).Customer!.Name.Should().Be("Customer-20");
        orders.Single(o => o.Id == 4).Customer!.Name.Should().Be("Customer-21");
    }

    // ── Collapse: logical names sharing a connection share one context instance ──
    [Fact]
    public void LogicalNamesSharingConnection_ResolveToSameContextInstance()
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;" };
        var shared = $"Data Source={Path.Combine(Path.GetTempPath(), $"mmca-collapse-{Guid.NewGuid():N}.db")}";
        var resolver = new DataSourceResolver(
            connectionStrings,
            new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
            {
                ["First"] = new() { SqliteConnectionString = shared },
                ["Second"] = new() { SqliteConnectionString = shared },
            }),
            NullLogger<DataSourceResolver>.Instance);

        var keyFirst = resolver.ResolveLogical(DataSource.Sqlite, "First");
        var keySecond = resolver.ResolveLogical(DataSource.Sqlite, "Second");
        keyFirst.Should().Be(keySecond, "equal connection strings must collapse to one physical source");

        var physicalFactory = new PhysicalDbContextFactory(_serviceProvider, resolver, new FixedAssemblyProvider());
        using var factory = new DbContextFactory(physicalFactory, _registry, resolver, Mock.Of<ICurrentUserService>());

        ReferenceEquals(factory.GetDbContext(keyFirst), factory.GetDbContext(keySecond)).Should().BeTrue(
            "one physical source must yield one context (one change tracker, one transaction)");
    }

    // ── Helpers ──
    private void CreateSchema(string logicalName)
    {
        var key = _resolver.ResolveLogical(DataSource.Sqlite, logicalName);
        _dbContextFactory.GetDbContext(key).Database.EnsureCreated();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp file cleanup.
        }
    }

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
            [typeof(MultiSourceSqliteIntegrationTests).Assembly];
    }

    // ── Test entities & configurations ──
    public sealed class MultiSourceOrder : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int CustomerId { get; set; }

        public MultiSourceCustomer? Customer { get; set; }
    }

    public sealed class MultiSourceCustomer : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed record MultiSourceTestEvent : MMCA.Common.Domain.Interfaces.IDomainEvent
    {
        public DateTime DateOccurred { get; init; } = DateTime.UtcNow;

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    [UseDatabase("SourceA")]
    private sealed class MultiSourceOrderConfiguration : EntityTypeConfigurationSqlite<MultiSourceOrder, int>;

    [UseDatabase("SourceB")]
    private sealed class MultiSourceCustomerConfiguration : EntityTypeConfigurationSqlite<MultiSourceCustomer, int>;
}
