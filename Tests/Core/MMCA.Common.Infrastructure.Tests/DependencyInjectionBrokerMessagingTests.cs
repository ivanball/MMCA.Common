using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Tests.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Registration-level tests for <c>AddBrokerMessaging</c>. They inspect the
/// <see cref="ServiceCollection"/> rather than building a provider: the broker branch registers an
/// EF-backed inbox store whose dependencies a unit test has no business standing up, and what is under
/// test here is which descriptors land and which topology is built, not what they resolve to.
/// </summary>
public sealed class DependencyInjectionBrokerMessagingTests
{
    public sealed record class OrderPlaced : BaseIntegrationEvent;

    public sealed record class OrderPlacedV1 : BaseIntegrationEvent;

    private static IConfiguration ConfigurationFor(
        string provider = InMemoryBrokerTransport.DefaultName,
        bool? enableInbox = null,
        params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["MessageBus:Provider"] = provider,
            ["applicationName"] = "Demo.Orders.Service",
            ["Application:Namespace"] = "demo",
        };
        if (enableInbox is { } inbox)
        {
            values["MessageBus:EnableInbox"] = inbox ? "true" : "false";
        }

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // The exact binding AddBrokerMessaging performs (see its Get<MessageBusSettings>() call): the
    // section is bound without options validation, so this is the path a setting really travels.
    private static MessageBusSettings Bind(IConfiguration configuration) =>
        configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()
            ?? new MessageBusSettings();

    private static ServiceCollection WithTransport(out InMemoryBrokerTransport transport)
    {
        var services = new ServiceCollection();
        transport = new InMemoryBrokerTransport();
        services.AddBrokerTransport(transport);
        return services;
    }

    private static bool HasHostedService<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));

    private static Type? InboxStoreImplementation(IServiceCollection services) =>
        services.FirstOrDefault(d => d.ServiceType == typeof(IInboxStore))?.ImplementationType;

    private static BrokerTopology Topology(IServiceCollection services) =>
        services.Select(d => d.ImplementationInstance).OfType<BrokerTopology>().Should().ContainSingle().Subject;

    [Fact]
    public void AddBrokerMessaging_InboxDisabled_RegistersNoOpStoreAndTheLoudWarningService()
    {
        var services = WithTransport(out _);

        services.AddBrokerMessaging(ConfigurationFor(enableInbox: false));

        InboxStoreImplementation(services).Should().Be<NoOpInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should()
            .BeTrue("a silently disabled dedup store is indistinguishable from an enabled one until a duplicate reaches a customer");
    }

    [Fact]
    public void AddBrokerMessaging_InboxEnabled_RegistersEfStoreAndNoWarningService()
    {
        var services = WithTransport(out _);

        services.AddBrokerMessaging(ConfigurationFor(enableInbox: true));

        InboxStoreImplementation(services).Should().Be<EfInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should()
            .BeFalse("nothing is off, so there is nothing to warn about");
    }

    [Fact]
    public void AddBrokerMessaging_InboxSettingOmitted_DefaultsToTheEfStoreUnderABroker()
    {
        // At-least-once delivery without dedup is not a default worth shipping.
        var services = WithTransport(out _);

        services.AddBrokerMessaging(ConfigurationFor());

        InboxStoreImplementation(services).Should().Be<EfInboxStore>();
        HasHostedService<InboxDisabledWarningService>(services).Should().BeFalse();
    }

    [Fact]
    public void AddBrokerMessaging_InProcessProvider_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddBrokerMessaging(ConfigurationFor("InProcess"), consumers => consumers.RegisterIntegrationEventConsumer<OrderPlaced>());

        services.Should().BeEmpty("the in-process provider short-circuits before touching the container");
    }

    [Theory]
    [InlineData("RabbitMq", "amqp://guest:guest@localhost:5672", null)]
    [InlineData("PostgreSql", "Host=localhost;Database=broker;Username=u;Password=p", null)]
    [InlineData(InMemoryBrokerTransport.DefaultName, null, null)]
    [InlineData(InMemoryBrokerTransport.DefaultName, null, "   ")]
    public void AddBrokerMessaging_BrokerProviderWithoutAnApplicationNamespace_FailsBeforeRegisteringAnything(
        string provider,
        string? connectionString,
        string? applicationNamespace)
    {
        // The host's application name is set, so the namespace WOULD resolve (to "Demo-Orders-Service"):
        // that per-service fallback is exactly what splits one application's services onto different topics.
        var services = WithTransport(out var transport);
        var descriptorsBefore = services.Count;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = provider,
                ["MessageBus:ConnectionString"] = connectionString,
                ["applicationName"] = "Demo.Orders.Service",
                ["Application:Namespace"] = applicationNamespace,
            })
            .Build();

        var act = () => services.AddBrokerMessaging(configuration, consumers => consumers.RegisterIntegrationEventConsumer<OrderPlaced>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{provider}'*Application:Namespace is not configured*every service of one application must share the same value*silently lost*Set Application:Namespace to the same value on every service of the application*Application__Namespace=*");
        services.Should().HaveCount(descriptorsBefore, "a refused broker registration leaves nothing half-registered");
        transport.RegisterCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("RabbitMq", "amqp://guest:guest@localhost:5672")]
    [InlineData("PostgreSql", "Host=localhost;Database=broker;Username=u;Password=p")]
    [InlineData(InMemoryBrokerTransport.DefaultName, null)]
    public void AddBrokerMessaging_BrokerProviderWithAnApplicationNamespace_PrefixesEveryTopicWithIt(string provider, string? connectionString)
    {
        var services = WithTransport(out _);

        services.AddBrokerMessaging(
            ConfigurationFor(provider, extra: connectionString is null ? [] : [("MessageBus:ConnectionString", connectionString)]),
            consumers => consumers.RegisterIntegrationEventConsumer<OrderPlaced>());

        var topology = Topology(services);
        topology.ApplicationNamespace.Should().Be("demo");
        topology.Subscriptions.Should().ContainSingle().Which.Topic.Should().StartWith("demo-");
    }

    [Fact]
    public void AddBrokerMessaging_InProcessProviderWithoutAnApplicationNamespace_RegistersNothingAndDoesNotThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MessageBus:Provider"] = "InProcess" })
            .Build();

        services.AddBrokerMessaging(configuration);

        services.Should().BeEmpty("an in-process host has no topics, so it needs no shared namespace");
    }

    [Fact]
    public void AddBrokerMessaging_BuildsTheTopology_QueueFromTheApplicationName_TopicsFromTheNamespace()
    {
        var services = WithTransport(out var transport);

        services.AddBrokerMessaging(ConfigurationFor(), consumers => consumers.RegisterIntegrationEventConsumer<OrderPlaced>());

        var topology = Topology(services);
        topology.QueueName.Should().Be("demo-orders", "the application name loses its -service suffix");
        topology.ApplicationNamespace.Should().Be("demo");
        topology.Subscriptions.Should().ContainSingle()
            .Which.Topic.Should().Be("demo-" + BrokerNaming.EventName(typeof(OrderPlaced)));
        transport.Context!.Topology.Should().BeSameAs(topology, "the transport provisions and receives exactly the registered topology");
        transport.RegisterCalls.Should().Be(1);
    }

    [Fact]
    public void AddBrokerMessaging_AConfiguredQueueName_Wins()
    {
        var services = WithTransport(out _);

        services.AddBrokerMessaging(ConfigurationFor(extra: ("MessageBus:QueueName", "Orders.Primary")));

        Topology(services).QueueName.Should().Be("orders-primary");
    }

    [Fact]
    public void AddBrokerMessaging_NoQueueNameAndNoApplicationName_FailsNamingTheSetting()
    {
        var services = WithTransport(out _);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = InMemoryBrokerTransport.DefaultName,
                ["Application:Namespace"] = "demo",
            })
            .Build();

        var act = () => services.AddBrokerMessaging(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:QueueName*");
    }

    [Fact]
    public void AddBrokerMessaging_RegistersOneScopedConsumerPerSubscription_AndThePipeline()
    {
        var services = WithTransport(out _);

        services.AddBrokerMessaging(
            ConfigurationFor(),
            consumers => consumers
                .RegisterIntegrationEventConsumer<OrderPlaced>()
                .RegisterUpcastedIntegrationEventConsumer<OrderPlacedV1>());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IntegrationEventConsumer<OrderPlaced>))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(UpcastingIntegrationEventConsumer<OrderPlacedV1>))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IBrokerReceivePipeline))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddBrokerMessaging_SameEventRegisteredTwice_FailsAtRegistration()
    {
        var services = WithTransport(out _);

        var act = () => services.AddBrokerMessaging(
            ConfigurationFor(),
            consumers => consumers
                .RegisterIntegrationEventConsumer<OrderPlaced>()
                .RegisterUpcastedIntegrationEventConsumer<OrderPlaced>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void AddBrokerMessaging_ResolvesThePipelineAndThePublisherFromTheTransport()
    {
        var services = WithTransport(out var transport);
        services.AddLogging();

        services.AddBrokerMessaging(ConfigurationFor(), consumers => consumers.RegisterIntegrationEventConsumer<OrderPlaced>());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBrokerReceivePipeline>().Should().BeOfType<BrokerReceivePipeline>();
        provider.GetRequiredService<IBrokerPublisher>().Should().BeSameAs(transport.Broker);
    }

    [Fact]
    public void AddBrokerMessaging_BackpressureSettings_BindAndRegisterCleanly()
    {
        var configuration = ConfigurationFor(InMemoryBrokerTransport.DefaultName, null, ("MessageBus:PrefetchCount", "16"), ("MessageBus:ConcurrentMessageLimit", "8"));
        var services = WithTransport(out var transport);

        var settings = Bind(configuration);
        settings.PrefetchCount.Should().Be(16);
        settings.ConcurrentMessageLimit.Should().Be(8);

        services.AddBrokerMessaging(configuration);

        transport.Context!.Settings.PrefetchCount.Should().Be(16, "the transport reads the knobs off the context it is handed");
        transport.Context.Settings.ConcurrentMessageLimit.Should().Be(8);
    }

    [Fact]
    public void AddBrokerMessaging_BackpressureSettingsOmitted_LeaveTheTransportDefaults()
    {
        // Unset must mean "whatever the transport does", not zero: a prefetch window of 0 would stall
        // the receiver, which is why the policy applies the values only when they are above 0.
        var settings = Bind(ConfigurationFor());

        settings.PrefetchCount.Should().BeNull();
        settings.ConcurrentMessageLimit.Should().BeNull();
    }
}
