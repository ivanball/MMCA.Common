using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>
/// The RabbitMQ transport's registration: a publisher always, a receiver only for a host that consumes,
/// the AMQP URI checked at startup, and the backpressure defaults. Nothing here opens a connection.
/// </summary>
public sealed class RabbitMqBrokerTransportTests
{
    private static readonly BrokerSubscription Subscription =
        new("order-placed", typeof(object), "demo-order-placed", BrokerConsumerKind.Current);

    private static BrokerTransportContext Context(string? connectionString, params BrokerSubscription[] subscriptions) => new(
        new MessageBusSettings { Provider = MessageBusProviders.RabbitMq },
        connectionString,
        new ConfigurationBuilder().Build(),
        new BrokerTopology("demo", "demo-billing", subscriptions));

    private static ServiceProvider Register(BrokerTransportContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IBrokerReceivePipeline>());
        new RabbitMqBrokerTransport().Register(services, context);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Transport_IsNamedRabbitMq_WithNoDelayCap()
    {
        var transport = new RabbitMqBrokerTransport();

        transport.Name.Should().Be(MessageBusProviders.RabbitMq);
        transport.MaxRedeliveryDelay.Should().BeNull("delay queues hold a message for any interval");
    }

    [Fact]
    public async Task Register_WithSubscriptions_AddsThePublisherAndOneReceiver()
    {
        await using var provider = Register(Context("amqp://guest:guest@localhost:5672", Subscription));

        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<RabbitMqPublisher>();
        provider.GetServices<IHostedService>().Should().ContainSingle().Which.Should().BeOfType<RabbitMqReceiver>();
    }

    [Fact]
    public async Task Register_WithoutSubscriptions_AddsNoReceiver()
    {
        await using var provider = Register(Context(connectionString: null));

        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<RabbitMqPublisher>();
        provider.GetServices<IHostedService>().Should().BeEmpty("a host that consumes nothing has no queue");
    }

    [Fact]
    public void Register_AContainerDisposedSynchronously_DisposesThePublisherAndReceiver()
    {
        var provider = Register(Context("amqp://guest:guest@localhost:5672", Subscription));
        provider.GetRequiredService<IBrokerPublisher>();
        provider.GetServices<IHostedService>().Should().ContainSingle();

        var act = provider.Dispose;

        act.Should().NotThrow("a host or test that disposes its container without await must not fail on the transport");
    }

    [Theory]
    [InlineData("amqp://user:pass@rabbit:5672/vhost")]
    [InlineData("AMQPS://rabbit")]
    public void ParseConnectionUri_AcceptsAmqpUris(string connectionString) =>
        RabbitMqBrokerTransport.ParseConnectionUri(connectionString).Should().Be(new Uri(connectionString));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ParseConnectionUri_None_KeepsTheClientDefault(string? connectionString) =>
        RabbitMqBrokerTransport.ParseConnectionUri(connectionString).Should().BeNull();

    [Theory]
    [InlineData("Endpoint=sb://example/")]
    [InlineData("http://rabbit:15672")]
    [InlineData("not a uri")]
    public void Register_ConnectionStringNotAnAmqpUri_ThrowsAtStartup(string connectionString)
    {
        var act = () => new RabbitMqBrokerTransport().Register(new ServiceCollection(), Context(connectionString, Subscription));

        act.Should().Throw<InvalidOperationException>().WithMessage("*RabbitMq*amqp://*MessageBus:ConnectionString*");
    }

    [Fact]
    public void Backpressure_Configured_IsApplied()
    {
        var settings = new MessageBusSettings { ConcurrentMessageLimit = 4, PrefetchCount = 10 };

        var concurrency = RabbitMqBrokerTransport.ResolveConcurrency(settings);

        concurrency.Should().Be(4);
        RabbitMqBrokerTransport.ResolvePrefetchCount(settings, concurrency).Should().Be(10);
    }

    [Fact]
    public void Backpressure_Unset_DefaultsToTheProcessorCountAndTwiceItsWindow()
    {
        var settings = new MessageBusSettings { ConcurrentMessageLimit = 0, PrefetchCount = -1 };

        var concurrency = RabbitMqBrokerTransport.ResolveConcurrency(settings);

        concurrency.Should().Be((ushort)Environment.ProcessorCount);
        RabbitMqBrokerTransport.ResolvePrefetchCount(settings, concurrency).Should().Be((ushort)(Environment.ProcessorCount * 2));
    }

    [Fact]
    public void Backpressure_AboveTheClientRange_IsClamped()
    {
        var settings = new MessageBusSettings { ConcurrentMessageLimit = 100_000, PrefetchCount = 100_000 };

        RabbitMqBrokerTransport.ResolveConcurrency(settings).Should().Be(ushort.MaxValue);
        RabbitMqBrokerTransport.ResolvePrefetchCount(settings, 1).Should().Be(ushort.MaxValue);
    }

    [Fact]
    public void ConnectionFactory_RecoversAutomatically()
    {
        var factory = RabbitMqConnectionSource.CreateFactory(new Uri("amqp://u:p@rabbit:5673/v"), "demo-billing (receive)");

        factory.AutomaticRecoveryEnabled.Should().BeTrue();
        factory.TopologyRecoveryEnabled.Should().BeTrue();
        factory.HostName.Should().Be("rabbit");
        factory.Port.Should().Be(5673);
        factory.VirtualHost.Should().Be("v");
        factory.ClientProvidedName.Should().Be("demo-billing (receive)");
    }
}
