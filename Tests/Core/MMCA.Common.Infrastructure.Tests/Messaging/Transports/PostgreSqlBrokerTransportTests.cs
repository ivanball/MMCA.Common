using AwesomeAssertions;
using MassTransit;
using MassTransit.SqlTransport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Moq;
using Npgsql;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports;

/// <summary>
/// The built-in PostgreSQL broker transport (<c>MessageBus:Provider=PostgreSql</c>): selected with no
/// registration call, configured with the same receive policies as every other transport, given the
/// shared data source when a configurator is registered, and creating its schema through MassTransit's
/// migration hosted service ahead of the bus. Building the bus opens no connection, so none of this
/// needs a server; the wire round trip lives in the PostgreSQL Testcontainers tier.
/// </summary>
public sealed class PostgreSqlBrokerTransportTests
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=broker;Username=app;Password=secret";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> PostgreSqlSettings(string provider = "PostgreSql") => new()
    {
        ["MessageBus:Provider"] = provider,
        ["MessageBus:ConnectionString"] = ConnectionString,
        ["MessageBus:EndpointPrefix"] = "pg-unit",
        ["MessageBus:PrefetchCount"] = "7",
    };

    [Fact]
    public void Name_IsTheProviderConstant() =>
        new PostgreSqlBrokerTransport().Name.Should().Be(MessageBusProviders.PostgreSql).And.Be("PostgreSql");

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    public async Task AddBrokerMessaging_PostgreSql_ResolvesTheBuiltInTransportWithNoRegistrationCall(string provider)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(PostgreSqlSettings(provider)));
        await using var serviceProvider = services.BuildServiceProvider();

        services.Should().NotContain(d => d.ServiceType == typeof(IBrokerTransport), "the transport is built in, like RabbitMQ");
        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
        serviceProvider.GetRequiredService<IBusControl>().Address.Scheme.Should().Be("db", "the SQL transport's host address scheme");
    }

    [Fact]
    public void AddBrokerMessaging_UnknownProvider_ListsPostgreSqlAmongTheAvailableProviders()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationWith(new()
        {
            ["MessageBus:Provider"] = "Kafka",
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Available providers: InProcess, RabbitMq, PostgreSql*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public async Task AddBrokerMessaging_PostgreSql_AppliesTheSharedRetryRedeliveryAndBackpressure(string? redelivery)
    {
        // The same probe BrokerTransportPoliciesTests uses: the built bus reports its consume pipe.
        // Redelivery is unconditional (the SQL transport delays messages natively), so only an
        // explicit [0] turns it off, exactly as on Azure Service Bus and SQS.
        var settings = PostgreSqlSettings();
        if (redelivery is not null)
        {
            settings["MessageBus:RedeliveryIntervalsSeconds:0"] = redelivery;
        }

        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(settings), bus => bus.AddConsumer<ProbeConsumer>());
        await using var serviceProvider = services.BuildServiceProvider();

        var probe = System.Text.Json.JsonSerializer.Serialize(serviceProvider.GetRequiredService<IBusControl>().GetProbeResult());
        probe.Should().ContainEquivalentOf("retry");
        probe.Should().Contain("\"prefetchCount\":7");
        if (redelivery is null)
        {
            probe.Should().ContainEquivalentOf("redeliver");
        }
        else
        {
            probe.Should().NotContainEquivalentOf("redeliver");
        }
    }

    [Fact]
    public void AddBrokerMessaging_PostgreSqlWithoutAConnectionString_FailsNamingBothKeys()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationWith(new()
        {
            ["MessageBus:Provider"] = "PostgreSql",
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MessageBus:ConnectionString*ConnectionStrings:messaging*");
    }

    [Fact]
    public void AddBrokerMessaging_PostgreSql_RegistersTheMigrationHostedServiceAheadOfTheBus()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(PostgreSqlSettings()));

        var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        var migration = hostedServices.IndexOf(typeof(SqlTransportMigrationHostedService));
        var bus = hostedServices.FindIndex(t => t?.Name == "MassTransitHostedService");

        migration.Should().BeGreaterThanOrEqualTo(0, "the transport tables must exist before the bus starts");
        bus.Should().BeGreaterThan(migration, "hosted services start in registration order, so the schema is created first");
    }

    [Fact]
    public async Task AddBrokerMessaging_PostgreSql_HandsTheMigrationTheConnectionStringSchemaAndRole()
    {
        var settings = PostgreSqlSettings();
        settings["MessageBus:PostgreSql:Schema"] = "broker";
        settings["MessageBus:PostgreSql:Role"] = "broker_role";
        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(settings));
        await using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<SqlTransportOptions>>().Value;
        options.ConnectionString.Should().Be(ConnectionString);
        options.Schema.Should().Be("broker");
        options.Role.Should().Be("broker_role");
    }

    [Fact]
    public async Task AddBrokerMessaging_PostgreSql_DefaultsTheSchemaAndRoleToTransport()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(PostgreSqlSettings()));
        await using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<SqlTransportOptions>>().Value;
        options.Schema.Should().Be("transport");
        options.Role.Should().Be("transport");
    }

    [Fact]
    public void AddBrokerMessaging_PostgreSqlWithMigrateOnStartupFalse_RegistersNoMigration()
    {
        var settings = PostgreSqlSettings();
        settings["MessageBus:PostgreSql:MigrateOnStartup"] = "false";
        var services = new ServiceCollection().AddLogging();

        services.AddBrokerMessaging(ConfigurationWith(settings));

        services.Should().NotContain(d => d.ImplementationType == typeof(SqlTransportMigrationHostedService));
        services.Should().NotContain(d => d.ServiceType == typeof(ISqlTransportDatabaseMigrator));
    }

    [Fact]
    public async Task AddBrokerMessaging_PostgreSqlWithADataSourceConfigurator_BuildsTheBusOnTheSharedDataSource()
    {
        // The IAM path: a registered configurator (an RDS or Cloud SQL token provider) must reach the
        // broker's connections too, so the bus asks the container for the shared data source keyed by
        // its connection string instead of building a private one from the string.
        var configurator = new RecordingConfigurator();
        var services = new ServiceCollection().AddLogging();
        services.AddNpgsqlDataSourceConfigurator(configurator);

        services.AddBrokerMessaging(ConfigurationWith(PostgreSqlSettings()));
        await using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<IBusControl>().Address.Scheme.Should().Be("db");
        configurator.Calls.Should().Be(1, "building the bus resolved the shared data source, which ran the configurator once");
        serviceProvider.GetKeyedService<NpgsqlDataSource>(ConnectionString).Should().NotBeNull();
    }

    [Fact]
    public void Configure_RejectsNullArguments()
    {
        var transport = new PostgreSqlBrokerTransport();
        var context = new BrokerTransportContext(new MessageBusSettings(), ConnectionString, ConfigurationWith([]));

        var noConfigurator = () => transport.Configure(null!, context);
        var noContext = () => transport.Configure(new Mock<IBusRegistrationConfigurator>().Object, null!);

        noConfigurator.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
    }

    /// <summary>A consumer, so <c>ConfigureEndpoints</c> builds a receive endpoint carrying the policies.</summary>
    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    /// <summary>The message the probe consumer handles.</summary>
    public sealed record ProbeMessage(int Id);

    private sealed class RecordingConfigurator : INpgsqlDataSourceConfigurator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Configure(NpgsqlDataSourceBuilder builder) => Interlocked.Increment(ref _calls);
    }
}
