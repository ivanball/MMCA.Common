namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// The publish half of a transport, registered as a singleton by <see cref="Transports.IBrokerTransport.Register"/>.
/// Called only by the framework's <c>BrokerMessageBus</c>, which the outbox processor drains through, so a
/// failure here surfaces to the outbox's retry and circuit breaker: throw on any publish that the broker did
/// not accept.
/// </summary>
public interface IBrokerPublisher
{
    /// <summary>
    /// Publishes <paramref name="message"/> to its topic, carrying the body and every header unchanged.
    /// When <c>MessageBus:ProvisionTopology</c> is set, a missing topic is created first.
    /// </summary>
    /// <param name="message">The serialized message.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the broker has accepted the message.</returns>
    Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken);
}
