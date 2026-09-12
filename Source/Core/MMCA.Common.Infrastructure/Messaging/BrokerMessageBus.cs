using System.Globalization;
using MassTransit;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;

// Aliased, not imported: MassTransit ships its own static MessageHeaders (its well-known transport
// header names), so the bare name is ambiguous in any file that speaks both vocabularies.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Infrastructure.Messaging;

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
/// continues across the broker hop. The request context it does NOT know about (who raised the
/// event, their roles, the tenant, the correlation id) is stamped here as the
/// <see cref="MessageHeaders"/> headers, and read back by <c>IntegrationEventConsumer</c> on the
/// other side. The outbox processor restores each row's captured context onto this bus's scope
/// before it publishes, so the headers describe the original request rather than the background
/// poll that delivered it.
/// </para>
/// </summary>
/// <param name="publishEndpoint">MassTransit endpoint the events are published through.</param>
/// <param name="currentUserService">
/// Supplies the principal stamped on the message. Defaulted, so a host or test that constructs this
/// bus with the endpoint alone keeps the previous behavior and publishes no identity headers.
/// </param>
/// <param name="tenantContext">Supplies the tenant stamped on the message, defaulted for the same reason.</param>
/// <param name="correlationContext">Supplies the correlation id stamped on the message, defaulted for the same reason.</param>
public sealed class BrokerMessageBus(
    IPublishEndpoint publishEndpoint,
    ICurrentUserService? currentUserService = null,
    ITenantContext? tenantContext = null,
    ICorrelationContext? correlationContext = null) : IMessageBus
{
    /// <inheritdoc />
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var headers = CaptureHeaders();

        // Publish using the runtime type so MassTransit routes by the concrete event class
        // rather than the IIntegrationEvent base interface (which has no consumers bound to it).
        //
        // A publish with nothing to stamp takes the plain overload it always took: the pipe is an
        // allocation per message, and a host that resolved no ambient context (a seeder, a worker
        // raising system events) would pay it for headers it never writes.
        return headers.Count == 0
            ? publishEndpoint.Publish(integrationEvent, integrationEvent.GetType(), cancellationToken)
            : publishEndpoint.Publish(
                integrationEvent,
                integrationEvent.GetType(),
                Pipe.Execute<PublishContext>(context => Stamp(context, headers)),
                cancellationToken);
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

    /// <summary>
    /// Snapshots the ambient context as the header pairs to stamp. Only values that are actually
    /// present are collected: an absent header reads on the consumer side as "the publisher had
    /// nothing to say", which is exactly what an older publisher or a system-raised event means, so
    /// writing an empty one would turn a non-answer into a false answer.
    /// </summary>
    private List<KeyValuePair<string, string>> CaptureHeaders()
    {
        List<KeyValuePair<string, string>> headers = [];

        if (tenantContext?.TenantId is { Length: > 0 } tenantId)
        {
            headers.Add(new KeyValuePair<string, string>(MessageHeaders.TenantId, tenantId));
        }

        if (currentUserService?.UserId is { } userId)
        {
            headers.Add(new KeyValuePair<string, string>(
                MessageHeaders.UserId,
                userId.ToString(CultureInfo.InvariantCulture)));
        }

        if (currentUserService is not null
            && Context.AmbientOrigin.FlattenRoles(currentUserService.Roles) is { Length: > 0 } roles)
        {
            headers.Add(new KeyValuePair<string, string>(MessageHeaders.UserRoles, roles));
        }

        if (correlationContext?.CorrelationId is { Length: > 0 } correlationId)
        {
            headers.Add(new KeyValuePair<string, string>(MessageHeaders.CorrelationId, correlationId));
        }

        return headers;
    }

    /// <summary>Writes the captured pairs onto the outgoing message.</summary>
    private static void Stamp(PublishContext context, List<KeyValuePair<string, string>> headers)
    {
        foreach ((string name, string value) in headers)
        {
            context.Headers.Set(name, value);
        }
    }
}
