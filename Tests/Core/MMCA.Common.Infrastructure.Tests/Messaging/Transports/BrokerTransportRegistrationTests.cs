using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports;

/// <summary>
/// The registration-time broker-transport extension point: <c>AddBrokerMessaging</c> resolves
/// <c>MessageBus:Provider</c> to a built-in transport or to an adapter-registered
/// <see cref="IBrokerTransport"/>, and a name with no transport fails at registration naming the
/// package and the call that would supply it.
/// </summary>
public sealed class BrokerTransportRegistrationTests
{
    private static IConfiguration ConfigurationFor(string provider, string? connectionString = "Endpoint=sb://example/") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = provider,
                ["MessageBus:ConnectionString"] = connectionString,
                ["MessageBus:RetryLimit"] = "3",
                ["applicationName"] = "Demo.Orders.Service",
            })
            .Build();

    [Fact]
    public void AddBrokerMessaging_AdapterProviderNotRegistered_ThrowsNamingThePackageAndTheCall()
    {
        var services = new ServiceCollection();

        var act = () => services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'AzureServiceBus'*MMCA.Common.Azure*AddAzureServiceBusTransport()*BEFORE AddBrokerMessaging*");
        services.Should().BeEmpty("the transport is resolved before anything is registered, so a failure leaves nothing half-registered");
    }

    [Fact]
    public void AddBrokerMessaging_UnknownProvider_ThrowsNamingTheGenericRegistration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddBrokerMessaging(ConfigurationFor("Kafka"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Kafka'*AddBrokerTransport(...)*Available providers: InProcess, RabbitMq, PostgreSql*");
    }

    [Fact]
    public void AddBrokerMessaging_RegisteredTransport_IsRegisteredOnceWithTheResolvedContext()
    {
        var services = new ServiceCollection();
        var transport = new RecordingTransport("Fake");
        var configuration = ConfigurationFor("fake", "fake://broker");

        services.AddBrokerTransport(transport);
        services.AddBrokerMessaging(configuration);

        transport.Calls.Should().Be(1, "the provider name compares case-insensitively");
        transport.Context!.ConnectionString.Should().Be("fake://broker");
        transport.Context.Settings.RetryLimit.Should().Be(3);
        transport.Context.Configuration.Should().BeSameAs(configuration);
        transport.Context.Topology.QueueName.Should().Be("demo-orders");
        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
    }

    [Fact]
    public void AddBrokerMessaging_TheTransportRegistersLast_AfterTheTopologyAndThePipeline()
    {
        var services = new ServiceCollection();
        var transport = new RecordingTransport("Fake");

        services.AddBrokerTransport(transport);
        services.AddBrokerMessaging(ConfigurationFor("Fake"));

        transport.ServicesAtRegister.Should().Contain(typeof(BrokerTopology));
        transport.ServicesAtRegister.Should().Contain(typeof(IBrokerReceivePipeline));
    }

    [Fact]
    public void AddBrokerMessaging_ConnectionStringFallsBackToTheMessagingConnectionString()
    {
        var services = new ServiceCollection();
        var transport = new RecordingTransport("Fake");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "Fake",
                ["ConnectionStrings:messaging"] = "fake://from-aspire",
                ["applicationName"] = "Demo",
            })
            .Build();

        services.AddBrokerTransport(transport);
        services.AddBrokerMessaging(configuration);

        transport.Context!.ConnectionString.Should().Be("fake://from-aspire");
    }

    [Fact]
    public void AddBrokerMessaging_TwoTransportsWithOneName_TheLastRegisteredWins()
    {
        var services = new ServiceCollection();
        var first = new RecordingTransport("Fake");
        var second = new OtherRecordingTransport("Fake");

        services.AddBrokerTransport(first);
        services.AddBrokerTransport(second);
        services.AddBrokerMessaging(ConfigurationFor("Fake"));

        first.Calls.Should().Be(0);
        second.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("RabbitMq", "amqp://guest:guest@localhost:5672", typeof(RabbitMqPublisher))]
    [InlineData("rabbitmq", "amqp://guest:guest@localhost:5672", typeof(RabbitMqPublisher))]
    [InlineData("PostgreSql", "Host=localhost;Database=broker;Username=u;Password=p", typeof(PostgreSqlPublisher))]
    [InlineData("postgresql", "Host=localhost;Database=broker;Username=u;Password=p", typeof(PostgreSqlPublisher))]
    public async Task AddBrokerMessaging_BuiltInProviders_ResolveToTheBuiltInTransports(string provider, string connectionString, Type publisherType)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddBrokerMessaging(ConfigurationFor(provider, connectionString));

        // Resolving the publisher opens nothing: both transports connect on first use.
        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IBrokerPublisher>().Should().BeOfType(publisherType);
        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
    }

    [Fact]
    public void AddBrokerTransport_SameTransportTypeTwice_RegistersOnce()
    {
        var services = new ServiceCollection();

        services.AddBrokerTransport(new RecordingTransport("Fake"));
        services.AddBrokerTransport(new RecordingTransport("Fake"));

        services.Count(d => d.ServiceType == typeof(IBrokerTransport)).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AddBrokerTransport_BlankName_Throws(string name)
    {
        var act = () => new ServiceCollection().AddBrokerTransport(new RecordingTransport(name));

        act.Should().Throw<ArgumentException>().WithMessage("*non-empty Name*");
    }

    [Fact]
    public void AddBrokerTransport_Null_Throws()
    {
        var act = () => new ServiceCollection().AddBrokerTransport(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BrokerTransportContext_RejectsMissingSettingsConfigurationOrTopology()
    {
        var configuration = new ConfigurationBuilder().Build();
        var topology = new BrokerTopology("demo", "orders", []);

        var noSettings = () => new BrokerTransportContext(null!, null, configuration, topology);
        var noConfiguration = () => new BrokerTransportContext(new MessageBusSettings(), null, null!, topology);
        var noTopology = () => new BrokerTransportContext(new MessageBusSettings(), null, configuration, null!);

        noSettings.Should().Throw<ArgumentNullException>();
        noConfiguration.Should().Throw<ArgumentNullException>();
        noTopology.Should().Throw<ArgumentNullException>();
    }

    private class RecordingTransport(string name) : IBrokerTransport
    {
        public string Name { get; } = name;

        public TimeSpan? MaxRedeliveryDelay => null;

        public int Calls { get; private set; }

        public BrokerTransportContext? Context { get; private set; }

        public IReadOnlyList<Type> ServicesAtRegister { get; private set; } = [];

        public void Register(IServiceCollection services, BrokerTransportContext context)
        {
            Calls++;
            Context = context;
            ServicesAtRegister = [.. services.Select(d => d.ServiceType)];
        }
    }

    private sealed class OtherRecordingTransport(string name) : RecordingTransport(name);
}
