using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging.Broker;

/// <summary>
/// Declares the integration events a broker-connected host consumes. Passed to the
/// <c>configureConsumers</c> callback of <c>AddBrokerMessaging</c>; each call adds one subscription to
/// the host's <see cref="BrokerTopology"/>, binding the event's topic to the host's single queue.
/// <para>
/// There is one queue per host, so two services consuming the same event never compete for one queue,
/// and there is no per-consumer endpoint configuration to get wrong. A consumer that exhausts its retries
/// and redeliveries is dead-lettered by the framework's receive pipeline, which logs it and counts it
/// (<c>broker.fault.count</c>) at that point, so no separate fault consumer is registered.
/// </para>
/// </summary>
public interface IBrokerConsumerBuilder
{
    /// <summary>
    /// Subscribes the host to <typeparamref name="TEvent"/> and delivers each message to every
    /// <see cref="MMCA.Common.Application.Interfaces.Events.IIntegrationEventHandler{TEvent}"/> registered in
    /// the host, through the inbox. Use one call per integration event type the host consumes.
    /// </summary>
    /// <typeparam name="TEvent">The integration event type.</typeparam>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> is already registered.</exception>
    IBrokerConsumerBuilder RegisterIntegrationEventConsumer<TEvent>()
        where TEvent : class, IIntegrationEvent;

    /// <summary>
    /// Subscribes the host to a RETIRED contract <typeparamref name="TEvent"/>, upcasting each message to
    /// its terminal successor and delivering it to the handlers registered for THAT contract (ADR-090).
    /// Pair it with <c>services.AddEventUpcaster&lt;TEvent, TNew, TUpcaster&gt;()</c> and a plain
    /// <see cref="RegisterIntegrationEventConsumer{TEvent}"/> for the current contract. Never register
    /// both kinds for the same type. With no upcaster registered it degrades to ordinary dispatch on the
    /// original type.
    /// </summary>
    /// <typeparam name="TEvent">The retired integration event type to drain.</typeparam>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> is already registered.</exception>
    IBrokerConsumerBuilder RegisterUpcastedIntegrationEventConsumer<TEvent>()
        where TEvent : class, IIntegrationEvent;
}
