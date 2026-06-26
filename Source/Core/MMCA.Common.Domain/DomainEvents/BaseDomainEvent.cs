using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.DomainEvents;

/// <summary>
/// Base record for all domain events. Uses <c>record class</c> for structural equality
/// so duplicate events can be detected. <see cref="DateOccurred"/> defaults to
/// creation time (UTC), not dispatch time — this captures when the business action happened.
/// </summary>
/// <remarks>
/// The creation-time default on <see cref="DateOccurred"/> is a deliberate domain-modelling choice,
/// not an oversight: a domain event's occurrence instant is, by definition, the moment the aggregate
/// raises it, so stamping it at construction is the correct event-sourcing / audit semantic. It is
/// intentionally distinct from infrastructure timestamps that must be deterministically testable
/// (audit fields, notification read-time), which are stamped from an injected <c>TimeProvider</c>.
/// Relocating this stamp by threading a clock through every aggregate would not improve the model.
/// </remarks>
public abstract record class BaseDomainEvent : IDomainEvent
{
    public DateTime DateOccurred { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Unique identifier for this event instance. Used for consumer-side idempotency: the inbox
    /// dedups already-processed messages by this id. Defaults to a new GUID at construction and is
    /// serialized with the event, so it survives the outbox → broker → consumer round-trip.
    /// </summary>
    public Guid MessageId { get; init; } = Guid.NewGuid();
}
