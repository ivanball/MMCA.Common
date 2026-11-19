using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces.Events;

/// <summary>
/// Handles an integration event published by another module. Unlike <see cref="IDomainEventHandler{T}"/>
/// (which handles intra-module domain events), integration event handlers react to cross-module
/// notifications — e.g., a Sales module handling <c>UserRegistered</c> from the Identity module.
/// <para>
/// Implementations are auto-discovered by Scrutor assembly scanning (singleton lifetime,
/// handlers create their own DI scopes internally) and dispatched in-process by
/// <see cref="IDomainEventDispatcher"/>, or by the broker consumers (<c>IntegrationEventConsumer</c>)
/// when a broker transport is configured.
/// </para>
/// </summary>
/// <typeparam name="TIntegrationEvent">The integration event type this handler processes.</typeparam>
public interface IIntegrationEventHandler<in TIntegrationEvent>
    where TIntegrationEvent : IIntegrationEvent
{
    /// <summary>
    /// Handles the integration event.
    /// </summary>
    /// <param name="integrationEvent">The integration event to handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(TIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
