using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.DomainEvents;

/// <summary>
/// Base class for integration event handlers, the cross-module sibling of
/// <see cref="SafeDomainEventHandler{TDomainEvent}"/>. It supplies the two blocks every handler
/// repeats: the DI scope preamble and the log-and-rethrow envelope.
/// <para>
/// <see cref="IIntegrationEventHandler{TIntegrationEvent}"/> implementations are registered as
/// singletons, so they cannot constructor-inject a scoped service such as <c>IUnitOfWork</c>. Each
/// handler therefore opens its own scope per delivery. This base runs
/// <see cref="HandleScopedAsync"/> inside an <see cref="IServiceScopeFactory.CreateScope"/>-derived
/// async scope and hands the subclass that scope's <see cref="IServiceProvider"/>, so a handler
/// body is only its own resolutions plus its own logic, and the scope is always disposed.
/// </para>
/// <para>
/// <see cref="HandleScopedAsync"/> runs inside an exception filter that writes one error log line
/// and then lets the exception propagate unchanged; <see cref="OperationCanceledException"/> passes
/// straight through without a log line, because host shutdown is not a delivery failure. This
/// matches <see cref="SafeDomainEventHandler{TDomainEvent}"/> exactly.
/// </para>
/// <para>
/// Propagating rather than swallowing is what makes the retry promise true: a handler that reports
/// success has its delivery acknowledged, so nothing retries and the side effect is lost with only
/// a log line to show for it. Letting the exception through hands the decision to the delivery
/// mechanism, which is built for exactly this: on the outbox path the message keeps its retry
/// count, backs off, and dead-letters after <c>Outbox:MaxRetries</c> attempts; on the broker path
/// the inbox row stays unprocessed and MassTransit redelivers and then moves the message to the
/// error queue. Delivery is therefore at-least-once and subclasses must be idempotent.
/// </para>
/// </summary>
/// <typeparam name="TIntegrationEvent">The integration event type this handler processes.</typeparam>
/// <param name="scopeFactory">Factory used to open one DI scope per delivery.</param>
/// <param name="logger">Logger used by the default <see cref="LogHandlerFailure"/> implementation.</param>
public abstract class ScopedIntegrationEventHandlerBase<TIntegrationEvent>(
    IServiceScopeFactory scopeFactory,
    ILogger logger) : IIntegrationEventHandler<TIntegrationEvent>
    where TIntegrationEvent : IIntegrationEvent
{
    /// <inheritdoc />
    public async Task HandleAsync(TIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        try
        {
            AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                await HandleScopedAsync(integrationEvent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && LogAndRethrow(ex, integrationEvent))
        {
            // Unreachable: the filter always returns false so the exception keeps propagating.
            // Cancellation short-circuits the filter and propagates without an error log.
            throw;
        }
    }

    /// <summary>
    /// Implement the integration event handling logic. Resolve scoped services from
    /// <paramref name="services"/>; the scope is opened before this call and disposed after it.
    /// Exceptions thrown here are logged by the base class and then propagate to the caller, so the
    /// delivery mechanism can redeliver the event. Implementations must be idempotent.
    /// </summary>
    /// <param name="integrationEvent">The integration event to handle, never <see langword="null"/>.</param>
    /// <param name="services">The service provider of the scope opened for this delivery.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task HandleScopedAsync(
        TIntegrationEvent integrationEvent,
        IServiceProvider services,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the one error log line for a failed delivery. Override to log the event's own
    /// identifiers through a source-generated <c>[LoggerMessage]</c> method; the override runs
    /// inside the exception filter, so it must not throw and must not rethrow.
    /// </summary>
    /// <param name="exception">The exception that failed the delivery.</param>
    /// <param name="integrationEvent">The integration event being handled.</param>
    protected virtual void LogHandlerFailure(Exception exception, TIntegrationEvent integrationEvent) =>
        logger.LogError(
            exception,
            "Integration event handler {HandlerType} failed for event {EventType}. The delivery mechanism will redeliver the event.",
            GetType().Name,
            typeof(TIntegrationEvent).Name);

    /// <summary>
    /// Logs the failure and always returns <see langword="false"/> so the exception propagates.
    /// Running as an exception filter keeps the log write ahead of any unwinding and leaves the
    /// original stack trace untouched.
    /// </summary>
    private bool LogAndRethrow(Exception exception, TIntegrationEvent integrationEvent)
    {
        LogHandlerFailure(exception, integrationEvent);

        return false;
    }
}
