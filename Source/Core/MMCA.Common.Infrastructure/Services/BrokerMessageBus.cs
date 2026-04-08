using MassTransit;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// <see cref="IMessageBus"/> implementation backed by MassTransit. Publishes integration events
/// to the configured broker (RabbitMQ in development, Azure Service Bus in production). Used by
/// extracted microservices in place of <see cref="InProcessMessageBus"/>.
/// <para>
/// This bus does NOT itself write to the outbox. The transactional outbox semantics are preserved
/// by the existing <c>OutboxProcessor</c>: integration events are persisted to <c>OutboxMessage</c>
/// inside the same DB transaction as the aggregate changes (via the
/// <c>DomainEventSaveChangesInterceptor</c>), then the <c>OutboxProcessor</c> drains them by
/// calling this bus.
/// </para>
/// <para>
/// MassTransit automatically propagates the current <see cref="System.Diagnostics.Activity"/>
/// trace context as <c>traceparent</c>/<c>tracestate</c> message headers, so distributed tracing
/// continues across the broker hop.
/// </para>
/// </summary>
public sealed class BrokerMessageBus(IPublishEndpoint publishEndpoint) : IMessageBus
{
    /// <inheritdoc />
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // Publish using the runtime type so MassTransit routes by the concrete event class
        // rather than the IIntegrationEvent base interface (which has no consumers bound to it).
        return publishEndpoint.Publish(integrationEvent, integrationEvent.GetType(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvents);

        foreach (var integrationEvent in integrationEvents)
        {
            await PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
