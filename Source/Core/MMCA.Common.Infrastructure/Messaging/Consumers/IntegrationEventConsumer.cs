using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Generic MassTransit consumer that bridges <see cref="IConsumer{TEvent}"/> to the existing
/// in-process <see cref="IIntegrationEventHandler{TEvent}"/> contract. Resolves all registered
/// handlers from the per-message DI scope and invokes them in order.
/// <para>
/// Application code keeps writing handlers as <c>IIntegrationEventHandler&lt;ProductVariantChanged&gt;</c>
/// — there's no MassTransit-specific consumer class to write per event type. The Phase 0
/// <c>ScanModuleApplicationServices</c> already auto-discovers <see cref="IIntegrationEventHandler{T}"/>
/// implementations as singletons; this adapter routes broker-delivered messages to them.
/// </para>
/// <para>
/// Register one consumer per integration event type via the
/// <c>RegisterIntegrationEventConsumer&lt;TEvent&gt;</c> extension on <see cref="IBusRegistrationConfigurator"/>
/// inside the <c>configureConsumers</c> callback passed to <c>AddBrokerMessaging</c>.
/// </para>
/// </summary>
/// <typeparam name="TEvent">The integration event type. Must implement <see cref="IIntegrationEvent"/>.</typeparam>
public sealed partial class IntegrationEventConsumer<TEvent>(
    IEnumerable<IIntegrationEventHandler<TEvent>> handlers,
    IInboxStore inbox,
    ILogger<IntegrationEventConsumer<TEvent>> logger) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <inheritdoc />
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var integrationEvent = context.Message;

        // The inbox key is the event's [EventName] identity when it declares one, and its short type
        // name otherwise, which is what every row written so far holds. An unannotated event
        // therefore keeps matching its existing rows exactly.
        var eventTypeName = EventNameResolver.GetInboxName(typeof(TEvent));

        // Consumer-side idempotency: at-least-once broker delivery can redeliver the same message.
        // If the inbox already recorded it, skip the handlers and ack. (Always true when the inbox
        // is disabled, so the handlers run exactly as they did before the inbox existed.)
        //
        // TryBegin also STAGES the inbox row in the scope's unit of work, unsaved. A handler that
        // calls SaveChangesAsync on that same scope therefore commits the row in the same
        // transaction as its own mutations: the window where a crash between "handler committed"
        // and "inbox written" reprocessed the whole event is closed by construction rather than by
        // asking every handler to be idempotent.
        if (!await inbox.TryBeginAsync(integrationEvent.MessageId, eventTypeName, context.CancellationToken).ConfigureAwait(false))
        {
            LogDuplicateSkipped(logger, eventTypeName, integrationEvent.MessageId);
            return;
        }

        var handlerCount = 0;

        foreach (var handler in handlers)
        {
            handlerCount++;
            try
            {
                await handler.HandleAsync(integrationEvent, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Discard the staged row first, so the failed attempt leaves neither a rejected
                // insert on the scope's context nor an inbox row that would make the redelivery
                // look like a duplicate.
                inbox.Abandon(integrationEvent.MessageId);

                // Rethrow so MassTransit applies the UseMessageRetry policy configured in
                // ConfigureBrokerTransport (exponential backoff, MessageBusSettings.RetryLimit
                // attempts) before the message is dead-lettered. Logging here gives operators
                // visibility into which handler failed without losing the exception.
                LogHandlerFailure(logger, ex, eventTypeName, handler.GetType().FullName ?? "<unknown>");
                throw;
            }
        }

        if (handlerCount == 0)
        {
            // No handler registered for this event in this process: log a warning so a
            // misconfigured consumer service is visible. Returning normally lets MassTransit
            // ack the message; the broker won't retry.
            LogNoHandlers(logger, eventTypeName);
        }

        // Persist the staged row unless a handler's own save already committed it. Either way the
        // message is recorded only on a successful consume: the failure path above rethrows.
        await inbox.CompleteAsync(integrationEvent.MessageId, eventTypeName, context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Integration event {EventType} (message {MessageId}) already processed — skipping (idempotent inbox)")]
    private static partial void LogDuplicateSkipped(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IIntegrationEventHandler<{EventType}> registered in this process — broker message acked without action")]
    private static partial void LogNoHandlers(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event handler {HandlerType} failed for {EventType}; MassTransit will apply the configured retry policy before dead-lettering")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, string eventType, string handlerType);
}
