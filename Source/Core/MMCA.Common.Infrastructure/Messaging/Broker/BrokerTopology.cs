namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// What a broker-connected host consumes: its one queue and the event subscriptions bound to it. Built
/// by <c>AddBrokerMessaging</c> from the <see cref="IBrokerConsumerBuilder"/> calls, registered as a
/// singleton, and handed to the transport, which provisions and receives from exactly this.
/// A host that consumes nothing still has a topology (an empty subscription list): it publishes, and its
/// transport creates no queue.
/// </summary>
public sealed class BrokerTopology
{
    private readonly Dictionary<string, BrokerSubscription> _byEventName;

    /// <summary>Initializes a new instance of the <see cref="BrokerTopology"/> class.</summary>
    /// <param name="applicationNamespace">The resolved application namespace topics are prefixed with.</param>
    /// <param name="queueName">The host's queue name (<see cref="BrokerNaming.Queue"/>).</param>
    /// <param name="subscriptions">The subscriptions, at most one per event name.</param>
    /// <exception cref="InvalidOperationException">Two subscriptions share an event name.</exception>
    public BrokerTopology(string applicationNamespace, string queueName, IReadOnlyList<BrokerSubscription> subscriptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(subscriptions);

        _byEventName = new Dictionary<string, BrokerSubscription>(StringComparer.Ordinal);
        foreach (var subscription in subscriptions)
        {
            if (!_byEventName.TryAdd(subscription.EventName, subscription))
            {
                throw new InvalidOperationException(
                    $"The integration event '{subscription.EventName}' ({subscription.EventType.FullName}) is registered with the broker twice. "
                    + "Register each event once: RegisterIntegrationEventConsumer for the current contract, or RegisterUpcastedIntegrationEventConsumer for a retired one, never both.");
            }
        }

        ApplicationNamespace = applicationNamespace;
        QueueName = queueName;
        Subscriptions = subscriptions;
    }

    /// <summary>Gets the application namespace topics are prefixed with.</summary>
    public string ApplicationNamespace { get; }

    /// <summary>Gets the host's queue name.</summary>
    public string QueueName { get; }

    /// <summary>Gets the subscriptions bound to the queue, in registration order.</summary>
    public IReadOnlyList<BrokerSubscription> Subscriptions { get; }

    /// <summary>The topic an event type is published on.</summary>
    /// <param name="eventType">The integration event type.</param>
    /// <returns>The topic name.</returns>
    public string TopicFor(Type eventType) => BrokerNaming.Topic(ApplicationNamespace, BrokerNaming.EventName(eventType));

    /// <summary>Finds the subscription for a delivered message's wire name.</summary>
    /// <param name="eventName">The <see cref="BrokerHeaders.EventName"/> value.</param>
    /// <param name="subscription">The subscription, when found.</param>
    /// <returns><see langword="true"/> when the host consumes that event.</returns>
    public bool TryGetSubscription(string eventName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BrokerSubscription? subscription) =>
        _byEventName.TryGetValue(eventName, out subscription);
}
