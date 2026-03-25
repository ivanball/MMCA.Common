using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Handles a specific type of domain event. Implementations are auto-discovered
/// by Scrutor assembly scanning and resolved from DI during dispatch.
/// </summary>
/// <typeparam name="TDomainEvent">The domain event type this handler processes.</typeparam>
public interface IDomainEventHandler<in TDomainEvent>
    where TDomainEvent : IDomainEvent
{
    /// <summary>
    /// Handles the domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(TDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
