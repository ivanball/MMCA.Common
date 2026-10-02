using System.Diagnostics.CodeAnalysis;
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
    /// <summary>The suffix an explicit endpoint name gets on the fault consumer's endpoint.</summary>
    private const string FaultEndpointSuffix = "-fault";

    extension(IBusRegistrationConfigurator x)
    {
        /// <summary>
        /// Registers a MassTransit consumer for <typeparamref name="TEvent"/> that delegates
        /// to all <see cref="MMCA.Common.Application.Interfaces.Events.IIntegrationEventHandler{TEvent}"/>
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
        /// <param name="configureEndpoint">
        /// Optional endpoint configuration applied, through MassTransit's <c>Endpoint(...)</c>, to this
        /// consumer and to its <see cref="FaultIntegrationEventConsumer{TEvent}"/>, so a host can give
        /// ONE consumer its own queue without renaming any other. <see langword="null"/> (the default)
        /// leaves both on the names the endpoint name formatter derives.
        /// <para>
        /// An explicit <c>Name</c> BYPASSES the endpoint name formatter, and with it the application
        /// prefix the formatter adds (SEC-Common-53). The name you set must therefore carry that prefix
        /// itself (for example <c>"myapp-order-placed"</c>), or two applications sharing one broker can
        /// end up consuming from the same queue.
        /// </para>
        /// <para>
        /// The fault consumer receives the same configuration with one difference: an explicit
        /// <c>Name</c> gets <c>"-fault"</c> appended (<c>"myapp-order-placed"</c> becomes
        /// <c>"myapp-order-placed-fault"</c>), so the two consumers never share a queue. Every other
        /// setting (concurrency, prefetch, topology) is applied to both unchanged.
        /// </para>
        /// </param>
        public IBusRegistrationConfigurator RegisterIntegrationEventConsumer<TEvent>(
            bool registerFaultConsumer = true,
            Action<IEndpointRegistrationConfigurator>? configureEndpoint = null)
            where TEvent : class, IIntegrationEvent
        {
            var consumer = x.AddConsumer<IntegrationEventConsumer<TEvent>>();
            if (configureEndpoint is not null)
            {
                consumer.Endpoint(configureEndpoint);
            }

            if (registerFaultConsumer)
            {
                AddFaultConsumer<TEvent>(x, configureEndpoint);
            }

            return x;
        }

        /// <summary>
        /// Registers a MassTransit consumer for a RETIRED contract <typeparamref name="TEvent"/> that
        /// upcasts each message to its terminal successor and delegates to the
        /// <see cref="MMCA.Common.Application.Interfaces.Events.IIntegrationEventHandler{T}"/> implementations
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
        /// <param name="configureEndpoint">
        /// Optional endpoint configuration applied, through MassTransit's <c>Endpoint(...)</c>, to this
        /// consumer and to its <see cref="FaultIntegrationEventConsumer{TEvent}"/>, so a host can give
        /// ONE consumer its own queue without renaming any other. <see langword="null"/> (the default)
        /// leaves both on the names the endpoint name formatter derives.
        /// <para>
        /// An explicit <c>Name</c> BYPASSES the endpoint name formatter, and with it the application
        /// prefix the formatter adds (SEC-Common-53). The name you set must therefore carry that prefix
        /// itself (for example <c>"myapp-order-placed"</c>), or two applications sharing one broker can
        /// end up consuming from the same queue.
        /// </para>
        /// <para>
        /// The fault consumer receives the same configuration with one difference: an explicit
        /// <c>Name</c> gets <c>"-fault"</c> appended (<c>"myapp-order-placed"</c> becomes
        /// <c>"myapp-order-placed-fault"</c>), so the two consumers never share a queue. Every other
        /// setting (concurrency, prefetch, topology) is applied to both unchanged.
        /// </para>
        /// </param>
        public IBusRegistrationConfigurator RegisterUpcastedIntegrationEventConsumer<TEvent>(
            bool registerFaultConsumer = true,
            Action<IEndpointRegistrationConfigurator>? configureEndpoint = null)
            where TEvent : class, IIntegrationEvent
        {
            var consumer = x.AddConsumer<UpcastingIntegrationEventConsumer<TEvent>>();
            if (configureEndpoint is not null)
            {
                consumer.Endpoint(configureEndpoint);
            }

            if (registerFaultConsumer)
            {
                AddFaultConsumer<TEvent>(x, configureEndpoint);
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

    /// <summary>
    /// Registers the fault consumer for <typeparamref name="TEvent"/>, applying the integration-event
    /// consumer's endpoint configuration (if any) with an explicit name suffixed by
    /// <see cref="FaultEndpointSuffix"/>.
    /// </summary>
    private static void AddFaultConsumer<TEvent>(
        IBusRegistrationConfigurator configurator,
        Action<IEndpointRegistrationConfigurator>? configureEndpoint)
        where TEvent : class, IIntegrationEvent
    {
        var faultConsumer = configurator.AddConsumer<FaultIntegrationEventConsumer<TEvent>>();
        if (configureEndpoint is not null)
        {
            faultConsumer.Endpoint(endpoint => configureEndpoint(new FaultEndpointConfigurator(endpoint)));
        }
    }

    /// <summary>
    /// Forwards every setting to the fault consumer's real endpoint configurator, except that an
    /// explicit <c>Name</c> is suffixed with <see cref="FaultEndpointSuffix"/>, so the fault consumer
    /// never shares the integration-event consumer's queue.
    /// </summary>
    [SuppressMessage(
        "Major Code Smell",
        "S2376:Write-only properties should not be used",
        Justification = "Implements MassTransit's IEndpointRegistrationConfigurator, whose members are set-only by design; an explicit implementation cannot add the getters the rule asks for.")]
    private sealed class FaultEndpointConfigurator(IEndpointRegistrationConfigurator inner) : IEndpointRegistrationConfigurator
    {
        string IEndpointRegistrationConfigurator.Name
        {
            set => inner.Name = value + FaultEndpointSuffix;
        }

        bool IEndpointRegistrationConfigurator.Temporary
        {
            set => inner.Temporary = value;
        }

        int? IEndpointRegistrationConfigurator.PrefetchCount
        {
            set => inner.PrefetchCount = value;
        }

        int? IEndpointRegistrationConfigurator.ConcurrentMessageLimit
        {
            set => inner.ConcurrentMessageLimit = value;
        }

        bool IEndpointRegistrationConfigurator.ConfigureConsumeTopology
        {
            set => inner.ConfigureConsumeTopology = value;
        }

        string IEndpointRegistrationConfigurator.InstanceId
        {
            set => inner.InstanceId = value;
        }

        void IEndpointRegistrationConfigurator.AddConfigureEndpointCallback(Action<IReceiveEndpointConfigurator>? callback) =>
            inner.AddConfigureEndpointCallback(callback);

        void IEndpointRegistrationConfigurator.AddConfigureEndpointCallback(Action<IRegistrationContext, IReceiveEndpointConfigurator>? callback) =>
            inner.AddConfigureEndpointCallback(callback);
    }
}
