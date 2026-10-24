using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports;

/// <summary>
/// The registration-time broker-transport extension point: <c>AddBrokerMessaging</c> resolves
/// <c>MessageBus:Provider</c> to the built-in RabbitMQ transport or to an adapter-registered
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
            })
            .Build();

    [Fact]
    public void AddBrokerMessaging_AdapterProviderNotRegistered_ThrowsNamingThePackageAndTheCall()
    {
        var services = new ServiceCollection();

        var act = () => services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'AzureServiceBus'*MMCA.Common.Azure*AddAzureServiceBusTransport()*BEFORE AddBrokerMessaging*");
        services.Should().BeEmpty("the transport is resolved before AddMassTransit, so a failure leaves no half-registered bus");
    }

    [Fact]
    public void AddBrokerMessaging_UnknownProvider_ThrowsNamingTheGenericRegistration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddBrokerMessaging(ConfigurationFor("Kafka"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Kafka'*AddBrokerTransport(...)*Available providers: InProcess, RabbitMq*");
    }

    [Fact]
    public void AddBrokerMessaging_RegisteredTransport_IsConfiguredOnceWithTheResolvedContext()
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
        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddBrokerMessaging_RabbitMq_BuildsTheBusWithoutConnecting(bool enableDelayedRedelivery)
    {
        // Building the bus runs the transport's factory callback (host, receive policies, endpoints)
        // but opens no connection, so the built-in transport is exercised end to end without a broker.
        var services = new ServiceCollection().AddLogging();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMq",
                ["MessageBus:ConnectionString"] = "amqp://guest:guest@localhost:5672",
                ["MessageBus:EnableDelayedRedelivery"] = enableDelayedRedelivery ? "true" : "false",
            })
            .Build();

        services.AddBrokerMessaging(configuration);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBusControl>().Address.Scheme.Should().Be("rabbitmq");
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
    public void BrokerTransportContext_RejectsMissingSettingsOrConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var noSettings = () => new BrokerTransportContext(null!, null, configuration);
        var noConfiguration = () => new BrokerTransportContext(new MessageBusSettings(), null, null!);

        noSettings.Should().Throw<ArgumentNullException>();
        noConfiguration.Should().Throw<ArgumentNullException>();
    }

    private class RecordingTransport(string name) : IBrokerTransport
    {
        public string Name { get; } = name;

        public int Calls { get; private set; }

        public BrokerTransportContext? Context { get; private set; }

        public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
        {
            Calls++;
            Context = context;
        }
    }

    private sealed class OtherRecordingTransport(string name) : RecordingTransport(name);
}
