using MassTransit;
using MMCA.Common.Domain.IntegrationEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// MassTransit registration extensions for the generic <see cref="IntegrationEventConsumer{TEvent}"/>
/// adapter. Call from inside the <c>configureConsumers</c> callback passed to
/// <c>AddBrokerMessaging</c> in the host's <c>Program.cs</c>.
/// </summary>
public static class IntegrationEventConsumerExtensions
{
    extension(IBusRegistrationConfigurator x)
    {
        /// <summary>
        /// Registers a MassTransit consumer for <typeparamref name="TEvent"/> that delegates
        /// to all <see cref="MMCA.Common.Application.Interfaces.IIntegrationEventHandler{TEvent}"/>
        /// implementations resolved from DI. Use one call per integration event type the
        /// service consumes.
        /// <para>
        /// A <see cref="FaultIntegrationEventConsumer{TEvent}"/> is registered alongside it by
        /// default. MassTransit publishes a <c>Fault&lt;TEvent&gt;</c> message whenever a consumer
        /// exhausts its retry policy; with nothing subscribed to that topic the only trace of an
        /// undelivered event is a row in the broker's <c>_error</c> queue that no dashboard is
        /// watching. The fault consumer subscribes to it and emits one Error log plus a
        /// <c>broker.fault.count</c> metric.
        /// </para>
        /// </summary>
        /// <typeparam name="TEvent">The integration event type.</typeparam>
        /// <param name="registerFaultConsumer">
        /// Whether to also register <see cref="FaultIntegrationEventConsumer{TEvent}"/> for this
        /// event. Defaults to <see langword="true"/>. Pass <see langword="false"/> for an event
        /// whose faults a host routes itself (a dedicated fault service, or a custom
        /// <c>IConsumer&lt;Fault&lt;TEvent&gt;&gt;</c>), so two consumers do not compete for the
        /// same fault topic.
        /// </param>
        public IBusRegistrationConfigurator RegisterIntegrationEventConsumer<TEvent>(
            bool registerFaultConsumer = true)
            where TEvent : class, IIntegrationEvent
        {
            x.AddConsumer<IntegrationEventConsumer<TEvent>>();

            if (registerFaultConsumer)
            {
                x.AddConsumer<FaultIntegrationEventConsumer<TEvent>>();
            }

            return x;
        }

        /// <summary>
        /// Registers a MassTransit consumer for a RETIRED contract <typeparamref name="TEvent"/> that
        /// upcasts each message to its terminal successor and delegates to the
        /// <see cref="MMCA.Common.Application.Interfaces.IIntegrationEventHandler{T}"/> implementations
        /// registered for THAT contract. Use it while the old type is still being published or is still
        /// sitting in a queue, so handlers only ever have to exist for the newest contract (ADR-090).
        /// <para>
        /// Pair it with <c>services.AddEventUpcaster&lt;TEvent, TNew, TUpcaster&gt;()</c>, which supplies
        /// the conversion, and with a plain
        /// <c>RegisterIntegrationEventConsumer&lt;TNew&gt;()</c> for the current contract. Do NOT also
        /// register the plain consumer for <typeparamref name="TEvent"/>: two consumers on one event
        /// compete for the same queue and would run the handlers twice.
        /// </para>
        /// <para>
        /// With no upcaster registered for <typeparamref name="TEvent"/> this degrades to ordinary
        /// handler dispatch on the original type, so the registration is safe to add before the
        /// upcaster exists and safe to leave in place for one release after it is deleted. Once the
        /// queues have drained, remove this call, the upcaster, and eventually the retired type.
        /// </para>
        /// </summary>
        /// <typeparam name="TEvent">The retired integration event type to drain.</typeparam>
        /// <param name="registerFaultConsumer">
        /// Whether to also register <see cref="FaultIntegrationEventConsumer{TEvent}"/> for this event.
        /// Same meaning as on <c>RegisterIntegrationEventConsumer&lt;TEvent&gt;</c>; defaults to
        /// <see langword="true"/>.
        /// </param>
        public IBusRegistrationConfigurator RegisterUpcastedIntegrationEventConsumer<TEvent>(
            bool registerFaultConsumer = true)
            where TEvent : class, IIntegrationEvent
        {
            x.AddConsumer<UpcastingIntegrationEventConsumer<TEvent>>();

            if (registerFaultConsumer)
            {
                x.AddConsumer<FaultIntegrationEventConsumer<TEvent>>();
            }

            return x;
        }

        /// <summary>
        /// Registers the consumer for <see cref="OutputCacheEvictionRequested"/>, the framework's
        /// cross-service output-cache eviction broadcast. Shorthand for
        /// <c>RegisterIntegrationEventConsumer&lt;OutputCacheEvictionRequested&gt;()</c>, named so the
        /// wiring reads as an intention rather than a type argument.
        /// <para>
        /// Pair it with <c>services.AddOutputCacheEvictionHandler()</c> from MMCA.Common.API, which
        /// registers the handler this consumer resolves. Registering the consumer without the
        /// handler is harmless but pointless: the messages are acked with a "no handler registered"
        /// log and nothing is evicted.
        /// </para>
        /// </summary>
        /// <param name="registerFaultConsumer">
        /// Whether to also register the fault consumer for the event. Same meaning as on
        /// <c>RegisterIntegrationEventConsumer&lt;TEvent&gt;</c>; defaults to <see langword="true"/>.
        /// </param>
        public IBusRegistrationConfigurator RegisterOutputCacheEvictionConsumer(
            bool registerFaultConsumer = true) =>
            x.RegisterIntegrationEventConsumer<OutputCacheEvictionRequested>(registerFaultConsumer);
    }
}
