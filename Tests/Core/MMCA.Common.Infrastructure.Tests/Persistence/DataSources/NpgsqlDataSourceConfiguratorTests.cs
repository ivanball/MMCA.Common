using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Moq;
using Npgsql;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DataSources;

/// <summary>
/// The optional <see cref="INpgsqlDataSourceConfigurator"/> extension point. Without one the
/// PostgreSQL context keeps handing EF the raw connection string; with one, every context for the same
/// connection string shares ONE configured <see cref="NpgsqlDataSource"/> (one pool, one password
/// provider), built once and disposed with the container. Nothing here connects: the assertions read
/// the options EF was given.
/// </summary>
public sealed class NpgsqlDataSourceConfiguratorTests
{
    private const string OrdersConnectionString = "Host=orders.example;Database=orders;Username=app";
    private const string BillingConnectionString = "Host=billing.example;Database=billing;Username=app";

    [Fact]
    public void WithoutAConfigurator_TheContextUsesTheConnectionStringPath()
    {
        using var provider = BuildProvider(configurator: null);
        using var context = CreateContext(provider, "Orders");

        var relational = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());

        relational.ConnectionString.Should().Be(OrdersConnectionString);
        DataSourceOf(context).Should().BeNull("no configurator means no data source is built at all");
        provider.GetKeyedService<NpgsqlDataSource>(OrdersConnectionString).Should().BeNull();
    }

    [Fact]
    public void WithAConfigurator_EveryContextForOneConnectionStringSharesOneDataSource()
    {
        var configurator = new CountingConfigurator();
        using var provider = BuildProvider(configurator);

        var orders = new List<NpgsqlDataSource?>();
        var billing = new List<NpgsqlDataSource?>();
        for (var i = 0; i < 5; i++)
        {
            using var ordersContext = CreateContext(provider, "Orders");
            using var billingContext = CreateContext(provider, "Billing");
            orders.Add(DataSourceOf(ordersContext));
            billing.Add(DataSourceOf(billingContext));
        }

        orders.Should().AllSatisfy(d => d.Should().BeSameAs(orders[0]));
        billing.Should().AllSatisfy(d => d.Should().BeSameAs(billing[0]));
        orders[0].Should().NotBeNull();
        orders[0].Should().NotBeSameAs(billing[0], "each distinct connection string gets its own data source");
        configurator.Calls.Should().Be(2, "the configurator runs once per data source, not once per context");
        configurator.Hosts.Should().BeEquivalentTo("orders.example", "billing.example");

        provider.GetKeyedService<NpgsqlDataSource>(OrdersConnectionString).Should().BeSameAs(
            orders[0],
            "the health check resolves the data source by its connection string and must get the context's");
    }

    [Fact]
    public void WithAConfigurator_TheContextNoLongerCarriesTheRawConnectionString()
    {
        using var provider = BuildProvider(new CountingConfigurator());
        using var context = CreateContext(provider, "Orders");

        var relational = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());

        relational.ConnectionString.Should().BeNull("EF is handed the data source instead");
        DataSourceOf(context)!.ConnectionString.Should().Contain("orders.example");
    }

    [Fact]
    public void TheDataSource_IsDisposedWithTheContainer()
    {
        var provider = BuildProvider(new CountingConfigurator());
        var dataSource = provider.GetRequiredKeyedService<NpgsqlDataSource>(OrdersConnectionString);

        provider.Dispose();

        var act = () => dataSource.OpenConnection();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void AddNpgsqlDataSourceConfigurator_IsIdempotentPerConfiguratorType()
    {
        var services = new ServiceCollection();

        services.AddNpgsqlDataSourceConfigurator(new CountingConfigurator());
        services.AddNpgsqlDataSourceConfigurator(new CountingConfigurator());

        services.Count(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(NpgsqlDataSource) && d.IsKeyedService).Should().Be(1);
    }

    private static NpgsqlDataSource? DataSourceOf(ApplicationDbContext context)
    {
        // The data source sits on the provider's own options extension, whose type lives in an EF
        // internal namespace; read it by name rather than taking a compile-time dependency on it.
        var extension = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());
        return (NpgsqlDataSource?)extension.GetType().GetProperty("DataSource")!.GetValue(extension);
    }

    private static ApplicationDbContext CreateContext(ServiceProvider provider, string logicalName)
    {
        var resolver = provider.GetRequiredService<IDataSourceResolver>();
        var factory = new PhysicalDbContextFactory(provider, resolver, new FixedAssemblyProvider());
        return factory.Create(resolver.ResolveLogical(DataSource.PostgreSQL, logicalName));
    }

    private static ServiceProvider BuildProvider(INpgsqlDataSourceConfigurator? configurator)
    {
        var connectionStrings = new ConnectionStringSettings { SQLServerConnectionString = "Server=unused;Database=unused;" };
        var dataSources = new DataSourcesSettings(new Dictionary<string, DataSourceEntrySettings>(StringComparer.Ordinal)
        {
            ["Orders"] = new() { PostgreSQLConnectionString = OrdersConnectionString },
            ["Billing"] = new() { PostgreSQLConnectionString = BillingConnectionString },
        });

        var resolver = new DataSourceResolver(Options.Create(connectionStrings), dataSources, NullLogger<DataSourceResolver>.Instance);
        var registry = new EntityDataSourceRegistry(new FixedAssemblyProvider(), resolver);

        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IDataSourceResolver>(resolver);

        if (configurator is not null)
        {
            services.AddNpgsqlDataSourceConfigurator(configurator);
        }

        return services.BuildServiceProvider();
    }

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }

    private sealed class CountingConfigurator : INpgsqlDataSourceConfigurator
    {
        private int _calls;

        public int Calls => _calls;

        public List<string?> Hosts { get; } = [];

        public void Configure(NpgsqlDataSourceBuilder builder)
        {
            Interlocked.Increment(ref _calls);
            lock (Hosts)
            {
                Hosts.Add(builder.ConnectionStringBuilder.Host);
            }
        }
    }
}
