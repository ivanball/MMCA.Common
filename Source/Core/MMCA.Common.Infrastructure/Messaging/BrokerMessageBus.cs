using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Shared.Messaging;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// <see cref="IMessageBus"/> implementation that publishes integration events to the broker
/// <c>MessageBus:Provider</c> selects (RabbitMQ and PostgreSQL are built in; an adapter such as
/// MMCA.Common.Azure or MMCA.Common.Aws registers its own transport), through the transport's
/// <see cref="IBrokerPublisher"/>. Used by extracted microservices in place of
/// <see cref="InProcessMessageBus"/>.
/// <para>
/// This bus does NOT itself write to the outbox. The transactional outbox semantics are preserved
/// by the existing <c>OutboxProcessor</c>: integration events are persisted to <c>OutboxMessage</c>
/// inside the same DB transaction as the aggregate changes (via the
/// <c>DomainEventSaveChangesInterceptor</c>), then the <c>OutboxProcessor</c> drains them by
/// calling this bus.
/// </para>
/// <para>
/// Each event is serialized as its RUNTIME type (System.Text.Json, the options the outbox uses) and
/// published on its topic (<see cref="BrokerTopology.TopicFor"/>) with the
/// <see cref="BrokerHeaders"/> transport headers (event name, message id, content type), a Producer
/// span on the <c>MMCA.Common.Messaging</c> source whose W3C <c>traceparent</c>/<c>tracestate</c>
/// travel as headers, and the request context as the <see cref="MessageHeaders"/> origin headers (who
/// raised the event, their roles, the tenant, the correlation id), read back by
/// <c>IntegrationEventConsumer</c> on the other side. The outbox processor restores each row's
/// captured context onto this bus's scope before it publishes, so the headers describe the original
/// request rather than the background poll that delivered it.
/// </para>
/// </summary>
/// <param name="publisher">The transport's publisher.</param>
/// <param name="topology">The host's topology, which names the topic of each event type.</param>
/// <param name="currentUserService">
/// Supplies the principal stamped on the message. Defaulted, so a host or test that constructs this
/// bus without it publishes no identity headers.
/// </param>
/// <param name="tenantContext">Supplies the tenant stamped on the message, defaulted for the same reason.</param>
/// <param name="correlationContext">Supplies the correlation id stamped on the message, defaulted for the same reason.</param>
public sealed class BrokerMessageBus(
    IBrokerPublisher publisher,
    BrokerTopology topology,
    ICurrentUserService? currentUserService = null,
    ITenantContext? tenantContext = null,
    ICorrelationContext? correlationContext = null) : IMessageBus
{
    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // The runtime type, not IIntegrationEvent: the topic, the wire name and the body all describe
        // the concrete event class the consumers subscribe to.
        var eventType = integrationEvent.GetType();
        var eventName = BrokerNaming.EventName(eventType);
        var topic = topology.TopicFor(eventType);
        var messageId = integrationEvent.MessageId;

        var headers = CaptureHeaders();
        headers[BrokerHeaders.EventName] = eventName;
        headers[BrokerHeaders.MessageId] = messageId.ToString("D", CultureInfo.InvariantCulture);
        headers[BrokerHeaders.ContentType] = BrokerHeaders.JsonContentType;

        using var activity = BrokerWire.ActivitySource.StartActivity(topic + " publish", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", BrokerWire.MessagingSystem);
            activity.SetTag("messaging.operation.type", "send");
            activity.SetTag("messaging.destination.name", topic);
            activity.SetTag("messaging.message.id", headers[BrokerHeaders.MessageId]);
        }

        // The producer span when one was started; otherwise the ambient one (the outbox row's), so a
        // publish with no listener attached still carries the trace across the broker.
        InjectTraceContext(activity ?? Activity.Current, headers);

        var body = JsonSerializer.SerializeToUtf8Bytes(integrationEvent, eventType, BrokerWire.JsonOptions);

        await publisher.PublishAsync(
            new OutgoingBrokerMessage(topic, eventName, messageId, body, headers),
            cancellationToken).ConfigureAwait(false);
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
    /// Writes the W3C trace context of <paramref name="activity"/> as headers. Only a W3C id is
    /// written: a hierarchical id has no <c>traceparent</c> form, and a consumer reading a malformed
    /// one would start a fresh trace anyway.
    /// </summary>
    private static void InjectTraceContext(Activity? activity, Dictionary<string, string> headers)
    {
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is not { } id)
        {
            return;
        }

        headers[BrokerHeaders.TraceParent] = id;
        if (activity.TraceStateString is { Length: > 0 } traceState)
        {
            headers[BrokerHeaders.TraceState] = traceState;
        }
    }

    /// <summary>
    /// Snapshots the ambient context as the header pairs to stamp. Only values that are actually
    /// present are collected: an absent header reads on the consumer side as "the publisher had
    /// nothing to say", which is exactly what an older publisher or a system-raised event means, so
    /// writing an empty one would turn a non-answer into a false answer.
    /// </summary>
    private Dictionary<string, string> CaptureHeaders()
    {
        Dictionary<string, string> headers = [with(StringComparer.Ordinal)];

        AddIfPresent(headers, MessageHeaders.TenantId, tenantContext?.TenantId);
        AddIfPresent(
            headers,
            MessageHeaders.UserId,
            currentUserService?.UserId is { } userId ? userId.ToString(CultureInfo.InvariantCulture) : null);

        if (currentUserService is not null)
        {
            AddIfPresent(headers, MessageHeaders.UserRoles, Context.AmbientOrigin.FlattenRoles(currentUserService.Roles));
            AddIfPresent(headers, MessageHeaders.UserPermissions, Context.AmbientOrigin.FlattenPermissions(currentUserService.User));
        }

        AddIfPresent(headers, MessageHeaders.CorrelationId, correlationContext?.CorrelationId);

        return headers;
    }

    /// <summary>Adds a header only when it has something to say (see <see cref="CaptureHeaders"/>).</summary>
    private static void AddIfPresent(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            headers[name] = value;
        }
    }
}
