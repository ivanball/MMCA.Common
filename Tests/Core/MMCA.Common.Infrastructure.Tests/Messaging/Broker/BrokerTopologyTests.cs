using AwesomeAssertions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.IntegrationEvents;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Consumers;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Broker;

/// <summary>
/// <see cref="BrokerTopology"/> and the <see cref="IBrokerConsumerBuilder"/> that fills it: one queue
/// per host, one subscription per consumed event, topics prefixed with the application namespace.
/// </summary>
public sealed class BrokerTopologyTests
{
    public sealed record class OrderPlaced : BaseIntegrationEvent;

    public sealed record class OrderPlacedV1 : BaseIntegrationEvent;

    [Fact]
    public void Builder_CollectsOneSubscriptionPerEvent_InRegistrationOrder()
    {
        var builder = new BrokerConsumerBuilder("demo");

        builder
            .RegisterIntegrationEventConsumer<OrderPlaced>()
            .RegisterUpcastedIntegrationEventConsumer<OrderPlacedV1>();

        builder.Subscriptions.Should().HaveCount(2);
        var current = builder.Subscriptions[0];
        current.EventType.Should().Be<OrderPlaced>();
        current.EventName.Should().Be(BrokerNaming.EventName(typeof(OrderPlaced)));
        current.Topic.Should().Be("demo-" + current.EventName);
        current.Kind.Should().Be(BrokerConsumerKind.Current);
        builder.Subscriptions[1].Kind.Should().Be(BrokerConsumerKind.Upcasted);
    }

    [Fact]
    public void Builder_SameEventTwice_Throws()
    {
        var builder = new BrokerConsumerBuilder("demo");
        builder.RegisterIntegrationEventConsumer<OrderPlaced>();

        var again = () => builder.RegisterIntegrationEventConsumer<OrderPlaced>();
        var otherKind = () => builder.RegisterUpcastedIntegrationEventConsumer<OrderPlaced>();

        again.Should().Throw<InvalidOperationException>().WithMessage("*registered with the broker twice*");
        otherKind.Should().Throw<InvalidOperationException>().WithMessage("*never both*");
    }

    [Fact]
    public void OutputCacheEvictionShorthand_SubscribesTheFrameworkEvent()
    {
        var builder = new BrokerConsumerBuilder("demo");

        builder.RegisterOutputCacheEvictionConsumer();

        builder.Subscriptions.Should().ContainSingle()
            .Which.EventType.Should().Be<OutputCacheEvictionRequested>();
    }

    [Fact]
    public void ConsumerTypeFor_MapsTheKindToTheClosedConsumer()
    {
        var builder = new BrokerConsumerBuilder("demo");
        builder.RegisterIntegrationEventConsumer<OrderPlaced>().RegisterUpcastedIntegrationEventConsumer<OrderPlacedV1>();

        BrokerConsumerBuilder.ConsumerTypeFor(builder.Subscriptions[0]).Should().Be<IntegrationEventConsumer<OrderPlaced>>();
        BrokerConsumerBuilder.ConsumerTypeFor(builder.Subscriptions[1]).Should().Be<UpcastingIntegrationEventConsumer<OrderPlacedV1>>();
    }

    [Fact]
    public void Topology_ResolvesASubscriptionByItsWireName()
    {
        var builder = new BrokerConsumerBuilder("demo");
        builder.RegisterIntegrationEventConsumer<OrderPlaced>();
        var sut = new BrokerTopology("demo", "orders", builder.Subscriptions);

        sut.TryGetSubscription(BrokerNaming.EventName(typeof(OrderPlaced)), out var found).Should().BeTrue();
        found!.EventType.Should().Be<OrderPlaced>();
        sut.TryGetSubscription("not-consumed", out _).Should().BeFalse();
        sut.QueueName.Should().Be("orders");
        sut.ApplicationNamespace.Should().Be("demo");
    }

    [Fact]
    public void Topology_TopicFor_MatchesTheSubscriptionTopic_SoPublisherAndConsumerAgree()
    {
        var builder = new BrokerConsumerBuilder("Demo.App");
        builder.RegisterIntegrationEventConsumer<OrderPlaced>();
        var sut = new BrokerTopology("Demo.App", "orders", builder.Subscriptions);

        sut.TopicFor(typeof(OrderPlaced)).Should().Be(builder.Subscriptions[0].Topic);
        sut.TopicFor(typeof(OrderPlaced)).Should().StartWith("demo-app-");
    }

    [Fact]
    public void Topology_TwoSubscriptionsWithOneWireName_Throws()
    {
        var subscription = new BrokerSubscription("order-placed", typeof(OrderPlaced), "demo-order-placed", BrokerConsumerKind.Current);

        var act = () => new BrokerTopology("demo", "orders", [subscription, subscription with { EventType = typeof(OrderPlacedV1) }]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'order-placed'*twice*");
    }

    [Fact]
    public void Topology_WithNoSubscriptions_IsValid() =>
        new BrokerTopology("demo", "publisher-only", []).Subscriptions.Should().BeEmpty();
}
