using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.DomainEvents;

/// <summary>
/// Base class for domain event handlers that must log their own failure with handler and event
/// context before the exception continues to the dispatcher. <see cref="HandleSafelyAsync"/> runs
/// inside an exception filter that writes one error log line and then lets the exception
/// propagate unchanged; <see cref="OperationCanceledException"/> passes straight through without
/// a log line, because host shutdown is not a delivery failure.
/// <para>
/// The base class used to swallow the exception, which made the "retried via the outbox" promise
/// false: a handler that reported success had its outbox row marked processed, so nothing ever
/// retried and the side effect was lost with only a log line to show for it. Propagating hands
/// the decision to the delivery mechanism instead, which is built for exactly this. On the
/// outbox-processor path the failed message keeps its retry count, backs off, and dead-letters
/// after <c>Outbox:MaxRetries</c> attempts.
/// </para>
/// <para>
/// Batch redelivery on the interceptor path is the consequence to design for. The save-changes
/// interceptor dispatches every local event of one save in a single call, so one rethrowing
/// handler aborts that call and skips the <c>MarkProcessedAsync</c> step for the WHOLE local
/// batch: every local event written by that save stays unprocessed and is redelivered by the
/// outbox processor, not just the event whose handler failed. Delivery is therefore at-least-once
/// and subclasses must be idempotent, tolerating both a repeat of their own event and a repeat of
/// every sibling event raised by the same save.
/// </para>
/// </summary>
/// <typeparam name="TDomainEvent">The domain event type this handler processes.</typeparam>
public abstract class SafeDomainEventHandler<TDomainEvent>(ILogger logger) : IDomainEventHandler<TDomainEvent>
    where TDomainEvent : BaseDomainEvent
{
    /// <inheritdoc />
    public async Task HandleAsync(TDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await HandleSafelyAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && LogAndRethrow(ex))
        {
            // Unreachable: the filter always returns false so the exception keeps propagating.
            throw;
        }
    }

    /// <summary>
    /// Implement the domain event handling logic. Exceptions thrown here are logged by the base
    /// class and then propagate to the caller, so the outbox can redeliver the event. Implementations
    /// must be idempotent (see the class remarks for the batch-redelivery contract).
    /// </summary>
    protected abstract Task HandleSafelyAsync(TDomainEvent domainEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Logs the failure and always returns <see langword="false"/> so the exception propagates.
    /// Running as an exception filter keeps the log write ahead of any unwinding and leaves the
    /// original stack trace untouched.
    /// </summary>
    private bool LogAndRethrow(Exception exception)
    {
        logger.LogError(
            exception,
            "Domain event handler {HandlerType} failed for event {EventType}. The outbox processor will redeliver the event.",
            GetType().Name,
            typeof(TDomainEvent).Name);

        return false;
    }
}
