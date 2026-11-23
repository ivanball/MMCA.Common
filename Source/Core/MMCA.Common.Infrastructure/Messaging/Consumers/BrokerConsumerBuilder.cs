using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// The <see cref="IBrokerConsumerBuilder"/> <c>AddBrokerMessaging</c> hands its
/// <c>configureConsumers</c> callback. Collects one <see cref="BrokerSubscription"/> per registered
/// event, in registration order, for the host's <see cref="BrokerTopology"/>.
/// </summary>
/// <param name="applicationNamespace">The resolved application namespace topics are prefixed with.</param>
internal sealed class BrokerConsumerBuilder(string applicationNamespace) : IBrokerConsumerBuilder
{
    private readonly List<BrokerSubscription> _subscriptions = [];

    /// <summary>Gets the subscriptions registered so far, in registration order.</summary>
    internal IReadOnlyList<BrokerSubscription> Subscriptions => _subscriptions;

    /// <inheritdoc />
    public IBrokerConsumerBuilder RegisterIntegrationEventConsumer<TEvent>()
        where TEvent : class, IIntegrationEvent =>
        Add(typeof(TEvent), BrokerConsumerKind.Current);

    /// <inheritdoc />
    public IBrokerConsumerBuilder RegisterUpcastedIntegrationEventConsumer<TEvent>()
        where TEvent : class, IIntegrationEvent =>
        Add(typeof(TEvent), BrokerConsumerKind.Upcasted);

    /// <summary>
    /// The closed consumer type the receive pipeline resolves for a subscription:
    /// <see cref="IntegrationEventConsumer{TEvent}"/> for a current contract,
    /// <see cref="UpcastingIntegrationEventConsumer{TEvent}"/> for a retired one.
    /// </summary>
    /// <param name="subscription">The subscription.</param>
    /// <returns>The closed consumer type, registered scoped by <c>AddBrokerMessaging</c>.</returns>
    internal static Type ConsumerTypeFor(BrokerSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var openType = subscription.Kind == BrokerConsumerKind.Upcasted
            ? typeof(UpcastingIntegrationEventConsumer<>)
            : typeof(IntegrationEventConsumer<>);

        return openType.MakeGenericType(subscription.EventType);
    }

    private BrokerConsumerBuilder Add(Type eventType, BrokerConsumerKind kind)
    {
        var eventName = BrokerNaming.EventName(eventType);

        var existing = _subscriptions.Find(s => s.EventType == eventType || string.Equals(s.EventName, eventName, StringComparison.Ordinal));
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"The integration event '{eventName}' ({eventType.FullName}) is registered with the broker twice "
                + $"(already as {existing.Kind}, for {existing.EventType.FullName}). Register each event once: "
                + "RegisterIntegrationEventConsumer for the current contract, or RegisterUpcastedIntegrationEventConsumer for a retired one, never both.");
        }

        _subscriptions.Add(new BrokerSubscription(eventName, eventType, BrokerNaming.Topic(applicationNamespace, eventName), kind));
        return this;
    }
}
