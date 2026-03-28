namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Marker interface for integration events — events that cross module boundaries.
/// Integration events extend <see cref="IDomainEvent"/> so they flow through the existing
/// outbox pipeline (at-least-once delivery), but are semantically distinct: they represent
/// facts that other modules need to react to, not internal domain state changes.
/// <para>
/// <b>Domain events</b> are intra-module (raised and handled within the same bounded context).
/// <b>Integration events</b> are inter-module (published by one module, consumed by others).
/// An event class can implement <see cref="IIntegrationEvent"/> to signal that it should be
/// handled by <c>IIntegrationEventHandler&lt;T&gt;</c> implementations in consuming modules.
/// </para>
/// </summary>
public interface IIntegrationEvent : IDomainEvent;
