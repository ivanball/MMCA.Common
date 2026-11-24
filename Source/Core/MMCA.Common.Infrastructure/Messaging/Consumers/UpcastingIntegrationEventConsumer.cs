using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Draining consumer for a RETIRED integration-event contract: receives <typeparamref name="TEvent"/>
/// from the host's queue, upcasts each message to its terminal contract through
/// <see cref="IEventUpcasterRegistry"/>, and invokes the handlers registered for THAT contract.
/// <para>
/// It exists so handlers are written once, against the newest event type, while producers that still
/// publish the old one (and messages already queued at the upgrade) keep being delivered. Register it
/// per retired type through <c>RegisterUpcastedIntegrationEventConsumer&lt;TEvent&gt;</c>; the current
/// contract keeps using the plain <see cref="IntegrationEventConsumer{TEvent}"/>. The builder refuses to
/// register both kinds for the same type.
/// </para>
/// <para>
/// Deduplication stays keyed on the ORIGINAL message id: the registry preserves the envelope across
/// every upcast hop, so a redelivery of the same broker message is recognised whatever contract the
/// handlers ultimately see. With no upcaster registered for <typeparamref name="TEvent"/> this
/// degrades to plain handler dispatch on the original type (ADR-090).
/// </para>
/// </summary>
/// <typeparam name="TEvent">The retired integration event type this queue is bound to.</typeparam>
public sealed partial class UpcastingIntegrationEventConsumer<TEvent>(
    IEventUpcasterRegistry upcasters,
    IServiceProvider serviceProvider,
    IInboxStore inbox,
    ILogger<UpcastingIntegrationEventConsumer<TEvent>> logger) : IBrokerMessageConsumer
    where TEvent : class, IIntegrationEvent
{
    /// <summary>
    /// Authentication type stamped on the identity rebuilt from the message headers. The same value
    /// <see cref="IntegrationEventConsumer{TEvent}"/> uses: an upcast changes the contract, never the
    /// hop the identity came back from.
    /// </summary>
    internal const string PrincipalAuthenticationType = IntegrationEventConsumer<TEvent>.PrincipalAuthenticationType;

    /// <summary>
    /// Consumes one delivered message of the retired contract: restores the publisher's origin,
    /// dedups on the original message id, upcasts to the terminal contract and runs its handlers.
    /// </summary>
    /// <param name="integrationEvent">The deserialized event of the retired contract.</param>
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

        // BEFORE the inbox is touched, exactly as IntegrationEventConsumer does it and for the same
        // reason: under database-per-tenant the inbox store's routing is decided by the tenant this
        // restores. A retired contract is delivered with the same headers as a current one, so a
        // consumer that skipped this would run its handlers anonymous while its sibling did not.
        // Held for the whole consume, so a handler's own scope inherits the origin too.
        using var origin = ConsumerOriginRestore.Apply(headers, serviceProvider, PrincipalAuthenticationType);

        // Dedup on the ORIGINAL message id, before any upcasting: the envelope survives every hop, so
        // this is the same id a plain IntegrationEventConsumer<TEvent> would have recorded.
        var messageId = integrationEvent.MessageId;

        // The inbox key is the retired contract's [EventName] identity when it declares one, and its
        // short type name otherwise, which is what every row written so far holds.
        var eventTypeName = EventNameResolver.GetInboxName(typeof(TEvent));

        // TryBegin also stages the inbox row in the scope's unit of work; it is written by
        // CompleteAsync once every handler has succeeded (see IntegrationEventConsumer).
        if (!await inbox.TryBeginAsync(messageId, eventTypeName, cancellationToken).ConfigureAwait(false))
        {
            LogDuplicateSkipped(logger, eventTypeName, messageId);
            return;
        }

        if (!upcasters.HasUpcasterFor(typeof(TEvent)))
        {
            // Degrade path: the host registered this consumer but no upcaster for the type, so the
            // registry returns the instance untouched and the handlers for TEvent run as usual.
            LogNoUpcaster(logger, eventTypeName);
        }

        var terminalEvent = upcasters.UpcastToTerminal(integrationEvent);
        var terminalType = terminalEvent.GetType();

        if (terminalType != typeof(TEvent))
        {
            LogUpcasted(logger, eventTypeName, terminalType.Name, messageId);
        }

        var (closedHandlerType, invoker) = UpcastDispatch.For(terminalType);

        var handlerCount = 0;

        foreach (var handler in serviceProvider.GetServices(closedHandlerType))
        {
            if (handler is null)
            {
                continue;
            }

            handlerCount++;

            try
            {
                await invoker(handler, terminalEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Discard the staged inbox row so the failed attempt leaves the scope's context
                // clean and the redelivery is reprocessed rather than skipped as a duplicate.
                inbox.Abandon(messageId);

                // Rethrow so the receive pipeline retries in a fresh scope, then redelivers or
                // dead-letters. Logging here gives operators visibility into which handler failed without losing the exception.
                LogHandlerFailure(logger, ex, terminalType.Name, handler.GetType().FullName ?? "<unknown>");
                throw;
            }
        }

        if (handlerCount == 0)
        {
            // No handler registered for the terminal contract in this process. Returning normally
            // completes the message; the broker will not retry.
            LogNoHandlers(logger, terminalType.Name, eventTypeName);
        }

        // Persist the staged row unless a handler's own save already committed it. Either way the
        // message is recorded only on a successful consume: the failure path above rethrows.
        await inbox.CompleteAsync(messageId, eventTypeName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    Task IBrokerMessageConsumer.ConsumeAsync(
        IIntegrationEvent integrationEvent,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken) =>
        ConsumeAsync((TEvent)integrationEvent, headers, cancellationToken);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Integration event {EventType} (message {MessageId}) already processed, skipping (idempotent inbox)")]
    private static partial void LogDuplicateSkipped(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upcasted broker message {MessageId} from retired contract {EventType} to {TerminalEventType}")]
    private static partial void LogUpcasted(ILogger logger, string eventType, string terminalEventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IEventUpcaster registered for {EventType}; UpcastingIntegrationEventConsumer is dispatching to handlers of the original contract")]
    private static partial void LogNoUpcaster(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IIntegrationEventHandler<{TerminalEventType}> registered in this process for upcasted {EventType}, broker message completed without action")]
    private static partial void LogNoHandlers(ILogger logger, string terminalEventType, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event handler {HandlerType} failed for {EventType}; the broker receive pipeline will retry and redeliver before dead-lettering")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, string eventType, string handlerType);
}
