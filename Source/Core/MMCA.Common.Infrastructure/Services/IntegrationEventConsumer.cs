using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

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
    ILogger<IntegrationEventConsumer<TEvent>> logger) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <inheritdoc />
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var integrationEvent = context.Message;
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
                // Let MassTransit retry per its configured retry policy. Logging here gives
                // operators visibility into which handler failed without losing the exception.
                LogHandlerFailure(logger, ex, typeof(TEvent).Name, handler.GetType().FullName ?? "<unknown>");
                throw;
            }
        }

        if (handlerCount == 0)
        {
            // No handler registered for this event in this process — log a warning so a
            // misconfigured consumer service is visible. Returning normally lets MassTransit
            // ack the message; the broker won't retry.
            LogNoHandlers(logger, typeof(TEvent).Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No IIntegrationEventHandler<{EventType}> registered in this process — broker message acked without action")]
    private static partial void LogNoHandlers(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event handler {HandlerType} failed for {EventType}; MassTransit will retry per its configured policy")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, string eventType, string handlerType);
}
