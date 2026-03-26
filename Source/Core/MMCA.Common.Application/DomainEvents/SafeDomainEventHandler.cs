using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.DomainEvents;

/// <summary>
/// Base class for domain event handlers that should not break the main transaction on failure.
/// Wraps <see cref="HandleSafelyAsync"/> in a try-catch that logs errors but does not propagate them.
/// Use this for side-effect handlers (e.g., sending emails, creating inventory) where failure
/// should be retried via the outbox rather than rolling back the primary operation.
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Domain event handler {HandlerType} failed for event {EventType}. The outbox processor will retry.",
                GetType().Name,
                typeof(TDomainEvent).Name);
        }
    }

    /// <summary>
    /// Implement the domain event handling logic. Exceptions thrown here are caught and logged
    /// by the base class without propagating to the caller.
    /// </summary>
    protected abstract Task HandleSafelyAsync(TDomainEvent domainEvent, CancellationToken cancellationToken);
}
