using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// One broker transport, selectable by name through <c>MessageBus:Provider</c>.
/// <para>
/// This is the whole broker boundary. <c>AddBrokerMessaging</c> owns everything that is the same on
/// every broker: the topology (which topics this host consumes, under which queue name), the wire
/// format, the receive pipeline (origin restore, inbox, retry, redelivery and dead-letter decisions),
/// the outbox-only publish path and the tracing. A transport only moves bytes: it publishes an
/// <see cref="Broker.OutgoingBrokerMessage"/> to a topic, delivers each message on the host's queue to
/// <see cref="Broker.IBrokerReceivePipeline"/>, and carries out the
/// <see cref="Broker.BrokerDisposition"/> the pipeline returns.
/// </para>
/// <para>
/// RabbitMQ and PostgreSQL are built in; every other transport ships in an adapter package
/// (<c>MMCA.Common.Azure</c> for Azure Service Bus, <c>MMCA.Common.Aws</c> for Amazon SQS/SNS) with one
/// registration method, so the core packages never reference a vendor SDK.
/// </para>
/// </summary>
public interface IBrokerTransport
{
    /// <summary>
    /// Gets the provider name a configuration file selects this transport by (<c>MessageBus:Provider</c>),
    /// compared case-insensitively. Stable and vendor-shaped, e.g. <c>AzureServiceBus</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the longest delay this broker can schedule a redelivery for, or <see langword="null"/> when
    /// it has no cap. The receive pipeline clamps every redelivery interval to it (Amazon SQS caps a
    /// message delay at 15 minutes).
    /// </summary>
    TimeSpan? MaxRedeliveryDelay { get; }

    /// <summary>
    /// Registers this transport's services: a singleton <see cref="Broker.IBrokerPublisher"/> and a hosted
    /// receiver for <see cref="BrokerTransportContext.Topology"/>'s queue that invokes
    /// <see cref="Broker.IBrokerReceivePipeline"/> per message and applies the returned disposition. When
    /// <see cref="MessageBusSettings.ProvisionTopology"/> is set, the receiver creates the topics, the
    /// queue and the topic-to-queue bindings idempotently before it starts receiving, and the publisher
    /// creates a missing topic on its first publish.
    /// <para>
    /// Called while <c>AddBrokerMessaging</c> runs, so configuration errors (a missing namespace or
    /// connection string) throw here, at startup, rather than at the first publish.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="context">The bound settings, the resolved connection string, the configuration and the topology.</param>
    void Register(IServiceCollection services, BrokerTransportContext context);
}
