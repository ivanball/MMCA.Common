using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.DomainEvents;

/// <summary>
/// Base record for integration events — events that cross module boundaries.
/// Inherits <see cref="BaseDomainEvent"/> for outbox pipeline compatibility and
/// implements <see cref="IIntegrationEvent"/> so the <c>DomainEventDispatcher</c>
/// routes them to <c>IIntegrationEventHandler&lt;T&gt;</c> implementations.
/// </summary>
public abstract record class BaseIntegrationEvent : BaseDomainEvent, IIntegrationEvent;
