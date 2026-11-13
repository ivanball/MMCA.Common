using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.DomainEvents;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Webhooks.Delivery;

/// <summary>
/// Turns one published integration event into one <see cref="DeliverWebhookInternalCommand"/> per
/// enabled subscription of the event's tenant that lists its webhook type (or <c>"*"</c>), all scheduled
/// inside ONE transaction. Registered by <c>AddWebhookEvent&lt;TEvent&gt;(name)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Inert unless webhooks are on: with no <see cref="WebhookSettings"/> registered, or
/// <see cref="WebhookSettings.Enabled"/> false, it returns without touching the store.
/// </para>
/// <para>
/// An event whose serialized payload exceeds <see cref="WebhookSettings.MaxPayloadBytes"/> is skipped
/// with one warning, because redelivering it cannot make it smaller. A failed schedule fails the
/// transaction and throws, so the outbox or the broker redelivers the event; the deterministic
/// delivery id keeps the redelivered rows' <c>webhook-id</c> unchanged.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The integration event contract.</typeparam>
internal sealed partial class WebhookFanOutHandler<TEvent> : ScopedIntegrationEventHandlerBase<TEvent>
    where TEvent : class, IIntegrationEvent
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="WebhookFanOutHandler{TEvent}"/> class.</summary>
    /// <param name="scopeFactory">Opens one scope per delivery.</param>
    /// <param name="logger">Receives the oversized-payload warning and the failure line.</param>
    public WebhookFanOutHandler(IServiceScopeFactory scopeFactory, ILogger<WebhookFanOutHandler<TEvent>> logger)
        : base(scopeFactory, logger) => _logger = logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookFanOutHandler{TEvent}"/> class in a container
    /// with no logging registered (the handler is registered by <c>AddWebhookEvent</c> in any host, and
    /// stays inert there until <c>AddWebhooks</c> turns it on).
    /// </summary>
    /// <param name="scopeFactory">Opens one scope per delivery.</param>
    public WebhookFanOutHandler(IServiceScopeFactory scopeFactory)
        : this(scopeFactory, NullLogger<WebhookFanOutHandler<TEvent>>.Instance)
    {
    }

    /// <inheritdoc />
    protected override async Task HandleScopedAsync(TEvent integrationEvent, IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = services.GetService<IOptions<WebhookSettings>>()?.Value;
        if (settings is not { Enabled: true }
            || !services.GetRequiredService<WebhookEventCatalog>().TryGetEventType(typeof(TEvent), out var eventType))
        {
            return;
        }

        var tenantId = services.GetRequiredService<ITenantContext>().TenantId;
        var subscriptions = await services.GetRequiredService<IWebhookSubscriptionStore>()
            .ListEnabledAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var matching = subscriptions.Where(subscription => subscription.Subscribes(eventType)).ToList();
        if (matching.Count == 0)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJsonOptions);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > settings.MaxPayloadBytes)
        {
            LogPayloadTooLarge(_logger, eventType, integrationEvent.MessageId, payloadBytes, settings.MaxPayloadBytes);
            return;
        }

        var commands = matching
            .Select(subscription => new DeliverWebhookInternalCommand(
                subscription.Id,
                WebhookDeliveryId.For(integrationEvent.MessageId, subscription.Id),
                eventType,
                integrationEvent.MessageId,
                integrationEvent.DateOccurred,
                payloadJson))
            .ToList();

        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var scheduler = services.GetRequiredService<IInternalCommandScheduler>();

        var scheduled = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var command in commands)
                {
                    var result = await scheduler.ScheduleAsync(command, cancellationToken: token).ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        return Result.Failure(result.Errors);
                    }
                }

                return Result.Success();
            },
            cancellationToken).ConfigureAwait(false);

        if (scheduled.IsFailure)
        {
            throw new InvalidOperationException(
                $"Webhook deliveries for event {integrationEvent.MessageId} ({eventType}) could not be scheduled: "
                + string.Join("; ", scheduled.Errors.Select(error => error.Message))
                + ". The event will be redelivered.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook event {EventType} {EventId} was not delivered: its payload is {PayloadBytes} bytes, over the {MaxPayloadBytes}-byte limit (Webhooks:MaxPayloadBytes)")]
    private static partial void LogPayloadTooLarge(ILogger logger, string eventType, Guid eventId, int payloadBytes, int maxPayloadBytes);
}
