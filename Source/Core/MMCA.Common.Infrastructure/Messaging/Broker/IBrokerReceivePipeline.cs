namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// The framework's receive pipeline, implemented once and registered by <c>AddBrokerMessaging</c>. A
/// transport's receiver calls it for every message delivered on the host's queue and applies the
/// returned <see cref="BrokerDisposition"/>.
/// <para>
/// It restores the publisher's trace context and origin, resolves the subscription from
/// <see cref="BrokerHeaders.EventName"/>, deserializes, runs the inbox-guarded handlers with in-process
/// exponential retry (a fresh scope per attempt), and decides between completing, a second-level
/// redelivery and dead-lettering. It never throws for a handler or payload failure; it throws only
/// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires, and the
/// transport then leaves the message unacknowledged so the broker delivers it again.
/// </para>
/// </summary>
public interface IBrokerReceivePipeline
{
    /// <summary>Processes one delivered message.</summary>
    /// <param name="message">The delivered message.</param>
    /// <param name="cancellationToken">Fires when the host is stopping.</param>
    /// <returns>What the transport must do with the message.</returns>
    Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken);
}
