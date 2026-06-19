using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.DomainEvents;

/// <summary>
/// Base record for integration events — events that cross module boundaries.
/// Inherits <see cref="BaseDomainEvent"/> for outbox pipeline compatibility and
/// implements <see cref="IIntegrationEvent"/> so the <c>DomainEventDispatcher</c>
/// routes them to <c>IIntegrationEventHandler&lt;T&gt;</c> implementations.
/// </summary>
public abstract record class BaseIntegrationEvent : BaseDomainEvent, IIntegrationEvent
{
    /// <summary>
    /// Schema version of this integration-event contract (ADR-010). Defaults to <c>1</c> and is
    /// serialized with the payload, so consumers have an explicit version signal to branch/upcast on.
    /// Additive/optional field changes keep the same version; a <i>breaking</i> change (renamed,
    /// removed, or retyped field) requires a NEW event type (e.g. <c>FooV2</c>) plus a consumer-side
    /// upcaster — never a silent reshape of an existing type. Concrete events bump it by overriding:
    /// <c>public override int SchemaVersion =&gt; 2;</c>. Declaring this <see langword="virtual"/> with a
    /// default keeps adding the member a non-breaking change for every existing event (they stay v1).
    /// </summary>
    public virtual int SchemaVersion => 1;
}
