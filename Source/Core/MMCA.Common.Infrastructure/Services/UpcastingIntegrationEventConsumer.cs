using System.Collections.Concurrent;
using System.Linq.Expressions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Inbox;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Draining consumer for a RETIRED integration-event contract: binds the broker queue to
/// <typeparamref name="TEvent"/>, upcasts each message to its terminal contract through
/// <see cref="IEventUpcasterRegistry"/>, and invokes the handlers registered for THAT contract.
/// <para>
/// It exists so handlers are written once, against the newest event type, while producers that still
/// publish the old one (and messages already queued at the upgrade) keep being delivered. Register it
/// per retired type through <c>RegisterUpcastedIntegrationEventConsumer&lt;TEvent&gt;</c>; the current
/// contract keeps using the plain <see cref="IntegrationEventConsumer{TEvent}"/>. Do NOT register both
/// consumers for the same type: they would compete for one queue and run the handlers twice.
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
    ILogger<UpcastingIntegrationEventConsumer<TEvent>> logger) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <summary>
    /// Caches the closed handler interface and its compiled invoker per terminal event type. The
    /// terminal type is only known at runtime (it depends on which upcasters the host registered), so
    /// handler resolution is non-generic; the compiled expression keeps reflection off the per-message
    /// path exactly as <c>DomainEventDispatcher</c> does for the in-process path.
    /// </summary>
    private static readonly ConcurrentDictionary<
        Type,
        (Type ClosedHandlerType, Func<object, object, CancellationToken, Task> Invoker)> DispatchCache = new();

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var integrationEvent = context.Message;

        // Dedup on the ORIGINAL message id, before any upcasting: the envelope survives every hop, so
        // this is the same id a plain IntegrationEventConsumer<TEvent> would have recorded.
        var messageId = integrationEvent.MessageId;

        if (await inbox.AlreadyProcessedAsync(messageId, context.CancellationToken).ConfigureAwait(false))
        {
            LogDuplicateSkipped(logger, typeof(TEvent).Name, messageId);
            return;
        }

        if (!upcasters.HasUpcasterFor(typeof(TEvent)))
        {
            // Degrade path: the host registered this consumer but no upcaster for the type, so the
            // registry returns the instance untouched and the handlers for TEvent run as usual.
            LogNoUpcaster(logger, typeof(TEvent).Name);
        }

        var terminalEvent = upcasters.UpcastToTerminal(integrationEvent);
        var terminalType = terminalEvent.GetType();

        if (terminalType != typeof(TEvent))
        {
            LogUpcasted(logger, typeof(TEvent).Name, terminalType.Name, messageId);
        }

        var (closedHandlerType, invoker) = DispatchCache.GetOrAdd(
            terminalType,
            static eventType => (
                typeof(IIntegrationEventHandler<>).MakeGenericType(eventType),
                BuildInvoker(eventType)));

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
                await invoker(handler, terminalEvent, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rethrow so MassTransit applies the UseMessageRetry policy configured in
                // ConfigureBrokerTransport before the message is dead-lettered. Logging here gives
                // operators visibility into which handler failed without losing the exception.
                LogHandlerFailure(logger, ex, terminalType.Name, handler.GetType().FullName ?? "<unknown>");
                throw;
            }
        }

        if (handlerCount == 0)
        {
            // No handler registered for the terminal contract in this process. Returning normally lets
            // MassTransit ack the message; the broker will not retry.
            LogNoHandlers(logger, terminalType.Name, typeof(TEvent).Name);
        }

        // Record processing AFTER handlers succeed so a handler failure (which rethrows above) leaves
        // the message un-recorded and eligible for MassTransit redelivery.
        await inbox.MarkProcessedAsync(messageId, typeof(TEvent).Name, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a compiled delegate that invokes <c>HandleAsync</c> on
    /// <c>IIntegrationEventHandler&lt;TTerminal&gt;</c> without boxing or reflection at call time. The
    /// expression tree casts the <see langword="object"/> parameters to their concrete types, then
    /// calls the strongly-typed method directly.
    /// </summary>
    /// <param name="eventType">The terminal event type to build the invoker for.</param>
    /// <returns>A delegate that accepts (handler, event, cancellationToken) as objects.</returns>
    private static Func<object, object, CancellationToken, Task> BuildInvoker(Type eventType)
    {
        var closedHandlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var method = closedHandlerType.GetMethod("HandleAsync")
            ?? throw new InvalidOperationException($"HandleAsync method not found on {closedHandlerType.Name}");

        // Build: (object handler, object integrationEvent, CancellationToken ct) =>
        //     ((IIntegrationEventHandler<TEvent>)handler).HandleAsync((TEvent)integrationEvent, ct)
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var eventParam = Expression.Parameter(typeof(object), "integrationEvent");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(
            Expression.Convert(handlerParam, closedHandlerType),
            method,
            Expression.Convert(eventParam, eventType),
            ctParam);

        return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
            call, handlerParam, eventParam, ctParam).Compile();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Integration event {EventType} (message {MessageId}) already processed, skipping (idempotent inbox)")]
    private static partial void LogDuplicateSkipped(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upcasted broker message {MessageId} from retired contract {EventType} to {TerminalEventType}")]
    private static partial void LogUpcasted(ILogger logger, string eventType, string terminalEventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IEventUpcaster registered for {EventType}; UpcastingIntegrationEventConsumer is dispatching to handlers of the original contract")]
    private static partial void LogNoUpcaster(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Information, Message = "No IIntegrationEventHandler<{TerminalEventType}> registered in this process for upcasted {EventType}, broker message acked without action")]
    private static partial void LogNoHandlers(ILogger logger, string terminalEventType, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event handler {HandlerType} failed for {EventType}; MassTransit will apply the configured retry policy before dead-lettering")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, string eventType, string handlerType);
}
