using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// Covers <see cref="MMCA.Common.Infrastructure.Persistence.Conventions.CrossDataSourceDegradeConvention"/>
/// (auto-degrade of relationships crossing physical data sources), the monolith-collapse
/// equivalence guarantee, and per-source EF model isolation via
/// <see cref="DataSourceModelCacheKeyFactory"/>.
/// </summary>
public sealed class CrossDataSourceDegradeConventionTests
{
    // ── Cross-source: FK + navigation removed, scalar column + index kept, foreign type gone ──
    [Fact]
    public void CrossSourceRelationship_IsDegraded()
    {
        var registry = new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal)
        {
            [typeof(DegradeOrder).FullName!] = new(DataSource.Sqlite, "CrossA"),
            [typeof(DegradeCustomer).FullName!] = new(DataSource.Sqlite, "CrossB"),
        });
        using var serviceProvider = BuildServiceProvider(registry);
        using var context = CreateContext(serviceProvider, "CrossA");

        var model = context.Model;

        // The foreign entity type is removed from this source's model entirely.
        model.FindEntityType(typeof(DegradeCustomer)).Should().BeNull();

        var order = model.FindEntityType(typeof(DegradeOrder))!;
        order.Should().NotBeNull();

        // The relationship and navigation are gone…
        order.GetForeignKeys().Should().BeEmpty();
        order.FindNavigation(nameof(DegradeOrder.Customer)).Should().BeNull();

        // …but the declared scalar FK column survives, with a compensating index.
        order.FindProperty(nameof(DegradeOrder.CustomerId)).Should().NotBeNull();
        order.GetIndexes().Should().Contain(index =>
            index.Properties.Count == 1 && index.Properties[0].Name == nameof(DegradeOrder.CustomerId));
    }

    // ── Monolith collapse: same source for everything → structural no-op ──
    [Fact]
    public void SameSourceRelationship_IsPreserved()
    {
        var registry = new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal)
        {
            [typeof(DegradeOrder).FullName!] = new(DataSource.Sqlite, "MonoA"),
            [typeof(DegradeCustomer).FullName!] = new(DataSource.Sqlite, "MonoA"),
        });
        using var serviceProvider = BuildServiceProvider(registry);
        using var context = CreateContext(serviceProvider, "MonoA");

        var model = context.Model;

        model.FindEntityType(typeof(DegradeCustomer)).Should().NotBeNull();

        var order = model.FindEntityType(typeof(DegradeOrder))!;
        order.GetForeignKeys().Should().ContainSingle(fk => fk.PrincipalEntityType.ClrType == typeof(DegradeCustomer));
        order.FindNavigation(nameof(DegradeOrder.Customer)).Should().NotBeNull();
    }

    [Fact]
    public void SameSourceModel_IsStructurallyEquivalentToUnregisteredBaseline()
    {
        // Baseline: nothing registered in the registry — the convention must treat every entity
        // as local and leave the model untouched (the pre-multi-database shape).
        using var baselineProvider = BuildServiceProvider(new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal)));
        using var baselineContext = CreateContext(baselineProvider, "MonoBaseline");

        var registry = new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal)
        {
            [typeof(DegradeOrder).FullName!] = new(DataSource.Sqlite, "MonoEquiv"),
            [typeof(DegradeCustomer).FullName!] = new(DataSource.Sqlite, "MonoEquiv"),
        });
        using var collapsedProvider = BuildServiceProvider(registry);
        using var collapsedContext = CreateContext(collapsedProvider, "MonoEquiv");

        var baseline = baselineContext.Model;
        var collapsed = collapsedContext.Model;

        collapsed.GetEntityTypes().Select(e => e.Name).Order(StringComparer.Ordinal)
            .Should().Equal(baseline.GetEntityTypes().Select(e => e.Name).Order(StringComparer.Ordinal));

        foreach (var baselineEntity in baseline.GetEntityTypes())
        {
            var collapsedEntity = collapsed.FindEntityType(baselineEntity.Name)!;
            collapsedEntity.GetForeignKeys().Count().Should().Be(baselineEntity.GetForeignKeys().Count());
            collapsedEntity.GetNavigations().Select(n => n.Name).Order(StringComparer.Ordinal)
                .Should().Equal(baselineEntity.GetNavigations().Select(n => n.Name).Order(StringComparer.Ordinal));
        }
    }

    // ── Model isolation: same context class, different physical sources → different models ──
    [Fact]
    public void SameContextClass_DifferentSources_BuildIsolatedModels()
    {
        var registry = new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal)
        {
            [typeof(DegradeOrder).FullName!] = new(DataSource.Sqlite, "IsoA"),
            [typeof(DegradeCustomer).FullName!] = new(DataSource.Sqlite, "IsoB"),
        });
        using var serviceProvider = BuildServiceProvider(registry);
        using var contextA = CreateContext(serviceProvider, "IsoA");
        using var contextB = CreateContext(serviceProvider, "IsoB");

        ReferenceEquals(contextA.Model, contextB.Model).Should().BeFalse(
            "each physical source must get its own EF model (DataSourceModelCacheKeyFactory)");

        contextA.Model.FindEntityType(typeof(DegradeOrder)).Should().NotBeNull();
        contextA.Model.FindEntityType(typeof(DegradeCustomer)).Should().BeNull();

        contextB.Model.FindEntityType(typeof(DegradeCustomer)).Should().NotBeNull();
        contextB.Model.FindEntityType(typeof(DegradeOrder)).Should().BeNull();
    }

    // ── DataSourceModelCacheKeyFactory unit behavior ──
    [Fact]
    public void ModelCacheKey_DiffersByPhysicalSourceName()
    {
        var registry = new MapRegistry(new Dictionary<string, DataSourceKey>(StringComparer.Ordinal));
        using var serviceProvider = BuildServiceProvider(registry);
        using var contextA = CreateContext(serviceProvider, "KeyA");
        using var contextB = CreateContext(serviceProvider, "KeyB");
        using var contextA2 = CreateContext(serviceProvider, "KeyA");

        var factory = new DataSourceModelCacheKeyFactory();

        factory.Create(contextA, designTime: false).Should().NotBe(factory.Create(contextB, designTime: false));
        factory.Create(contextA, designTime: false).Should().Be(factory.Create(contextA2, designTime: false));
        factory.Create(contextA, designTime: true).Should().NotBe(factory.Create(contextA, designTime: false));
    }

    // ── Helpers ──
    private static DegradeTestContext CreateContext(IServiceProvider serviceProvider, string sourceName)
    {
        var physical = new PhysicalDataSource(
            new DataSourceKey(DataSource.Sqlite, sourceName), "DataSource=:memory:", null, "AtlDevCon");

        var options = new DbContextOptionsBuilder<DegradeTestContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        return new DegradeTestContext(options, serviceProvider, physical);
    }

    private static ServiceProvider BuildServiceProvider(IEntityDataSourceRegistry registry) =>
        new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton(registry)
            .BuildServiceProvider();

    private sealed class MapRegistry(Dictionary<string, DataSourceKey> map) : IEntityDataSourceRegistry
    {
        public DataSourceKey GetDataSourceKey(Type entityType) => map[entityType.FullName!];

        public DataSourceKey GetDataSourceKey(string entityFullName) => map[entityFullName];

        public bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key) =>
            map.TryGetValue(entityFullName, out key);

        public IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse() => [.. map.Values.Distinct()];
    }

    private sealed class EmptyAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }

    public sealed class DegradeOrder : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int CustomerId { get; set; }

        public DegradeCustomer? Customer { get; set; }
    }

    public sealed class DegradeCustomer : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public List<DegradeOrder> Orders { get; } = [];
    }

    /// <summary>
    /// Mirrors <c>ApplyConfigurationsForEntitiesInContext</c>: only entities belonging to this
    /// context's physical source are registered explicitly; foreign entities enter the model
    /// solely via EF's relationship-discovery conventions (Order→Customer / Customer.Orders),
    /// which the finalizing convention then degrades when the ends' sources differ.
    /// </summary>
    public sealed class DegradeTestContext : ApplicationDbContext
    {
        private readonly IServiceProvider _testServiceProvider;

        public DegradeTestContext(
            DbContextOptions<DegradeTestContext> options,
            IServiceProvider serviceProvider,
            PhysicalDataSource physicalDataSource)
            : base(options, serviceProvider, new EmptyAssemblyProvider(), physicalDataSource) =>
            _testServiceProvider = serviceProvider;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var registry = _testServiceProvider.GetRequiredService<IEntityDataSourceRegistry>();

            if (IsLocal(registry, typeof(DegradeOrder)))
            {
                modelBuilder.Entity<DegradeOrder>();
            }

            if (IsLocal(registry, typeof(DegradeCustomer)))
            {
                modelBuilder.Entity<DegradeCustomer>();
            }

            base.OnModelCreating(modelBuilder);
        }

        private bool IsLocal(IEntityDataSourceRegistry registry, Type entityType) =>
            !registry.TryGetDataSourceKey(entityType.FullName!, out var key) || key == DataSourceKey;
    }
}
