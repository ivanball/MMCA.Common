using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Generic broker consumer that bridges a delivered <typeparamref name="TEvent"/> to the existing
/// in-process <see cref="IIntegrationEventHandler{TEvent}"/> contract. Resolves all registered
/// handlers from the per-attempt DI scope and invokes them in order.
/// <para>
/// Application code keeps writing handlers as <c>IIntegrationEventHandler&lt;ProductVariantChanged&gt;</c>;
/// there is no broker-specific consumer class to write per event type.
/// <c>ScanModuleApplicationServices</c> already auto-discovers <see cref="IIntegrationEventHandler{T}"/>
/// implementations; this adapter routes broker-delivered messages to them.
/// </para>
/// <para>
/// Registered per integration event type through
/// <see cref="Broker.IBrokerConsumerBuilder.RegisterIntegrationEventConsumer{TEvent}"/> inside the
/// <c>configureConsumers</c> callback passed to <c>AddBrokerMessaging</c>. The framework's receive
/// pipeline deserializes the message, opens a fresh scope per attempt, resolves this consumer from it
/// and calls <see cref="ConsumeAsync"/>; a throw from here is what drives the pipeline's retry,
/// redelivery and dead-letter decisions.
/// </para>
/// </summary>
/// <typeparam name="TEvent">The integration event type. Must implement <see cref="IIntegrationEvent"/>.</typeparam>
/// <param name="handlers">The handlers registered for this event in this process.</param>
/// <param name="inbox">Consumer-side idempotency store.</param>
/// <param name="logger">Logger for consume diagnostics.</param>
/// <param name="serviceProvider">
/// The per-attempt scope, onto which the publisher's context is restored before anything reads it
/// (see <see cref="ConsumerOriginRestore"/>). Taken as the provider rather than as the three
/// services themselves because one of them (<c>ScopedUserOverride</c>) is internal, and this
/// consumer is public: an internal type cannot appear in a public constructor's signature.
/// Defaulted, so a test constructing the consumer with the first three arguments still compiles
/// and simply restores nothing.
/// </param>
public sealed partial class IntegrationEventConsumer<TEvent>(
    IEnumerable<IIntegrationEventHandler<TEvent>> handlers,
    IInboxStore inbox,
    ILogger<IntegrationEventConsumer<TEvent>> logger,
    IServiceProvider? serviceProvider = null) : IBrokerMessageConsumer
    where TEvent : class, IIntegrationEvent
{
    /// <summary>
    /// Authentication type stamped on the identity rebuilt from the message headers. It is what
    /// makes <c>IsAuthenticated</c> true, and it names the hop the identity came back from.
    /// </summary>
    internal const string PrincipalAuthenticationType = "IntegrationEvent";

    /// <summary>
    /// Consumes one delivered event: restores the publisher's origin from <paramref name="headers"/>,
    /// skips a message the inbox already recorded, runs every handler, and records the message.
    /// </summary>
    /// <param name="integrationEvent">The deserialized event.</param>
    /// <param name="headers">The delivered message's headers (origin headers included).</param>
    /// <param name="cancellationToken">Fires when the host is stopping.</param>
    /// <returns>A task that completes when every handler has run.</returns>
    public async Task ConsumeAsync(
        TEvent integrationEvent,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(headers);

        // BEFORE the inbox is touched, not after: the inbox store resolves its context through the
        // scoped factory, and under database-per-tenant that routing is decided by the tenant this
        // restores. A message published with no origin headers restores nothing, and this scope
        // keeps the defaults it already had.
        // The handle stays open for the whole consume, so a handler that opens its own scope from the
        // root factory (ScopedIntegrationEventHandlerBase) still runs as the publisher's origin.
        using var origin = serviceProvider is not null
            ? ConsumerOriginRestore.Apply(headers, serviceProvider, PrincipalAuthenticationType)
            : null;

        // The inbox key is the event's [EventName] identity when it declares one, and its short type
        // name otherwise, which is what every row written so far holds. An unannotated event
        // therefore keeps matching its existing rows exactly.
        var eventTypeName = EventNameResolver.GetInboxName(typeof(TEvent));

        // Consumer-side idempotency: at-least-once broker delivery can redeliver the same message.
        // If the inbox already recorded it, skip the handlers and complete. (Always true when the
        // inbox is disabled, so the handlers run exactly as they did before the inbox existed.)
        //
        // TryBegin also STAGES the inbox row in this scope's unit of work, unsaved. The framework's
        // handlers do not save on this scope: they are singletons that open their own scope per
        // delivery (ScopedIntegrationEventHandlerBase), so the row is written by CompleteAsync below,
        // once every handler has succeeded. A crash between a handler's commit and that write
        // redelivers the event, which is why handlers must be idempotent.
        if (!await inbox.TryBeginAsync(integrationEvent.MessageId, eventTypeName, cancellationToken).ConfigureAwait(false))
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
                await handler.HandleAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Discard the staged row first, so the failed attempt leaves neither a rejected
                // insert on the scope's context nor an inbox row that would make the retry look like
                // a duplicate.
                inbox.Abandon(integrationEvent.MessageId);

                // Rethrow so the receive pipeline retries in a fresh scope (exponential backoff,
                // MessageBusSettings.RetryLimit retries), then redelivers or dead-letters. Logging
                // here gives operators visibility into which handler failed without losing the
                // exception.
                LogHandlerFailure(logger, ex, eventTypeName, handler.GetType().FullName ?? "<unknown>");
                throw;
            }
        }

        if (handlerCount == 0)
        {
            // No handler registered for this event in this process: log it at Information so a
            // misconfigured consumer service is visible. Returning normally completes the message;
            // the broker won't retry.
            LogNoHandlers(logger, eventTypeName);
        }

        // Persist the staged row unless a handler's own save already committed it. Either way the
        // message is recorded only on a successful consume: the failure path above rethrows.
        await inbox.CompleteAsync(integrationEvent.MessageId, eventTypeName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    Task IBrokerMessageConsumer.ConsumeAsync(
        IIntegrationEvent integrationEvent,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken) =>
        ConsumeAsync((TEvent)integrationEvent, headers, cancellationToken);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Integration event {EventType} (message {MessageId}) already processed, skipping (idempotent inbox)")]
    private static partial void LogDuplicateSkipped(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IIntegrationEventHandler<{EventType}> registered in this process, broker message completed without action")]
    private static partial void LogNoHandlers(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event handler {HandlerType} failed for {EventType}; the broker receive pipeline will retry and redeliver before dead-lettering")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, string eventType, string handlerType);
}
