using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.DomainEvents;

/// <summary>
/// Base record for all domain events. Uses <c>record class</c> for structural equality
/// so duplicate events can be detected. <see cref="DateOccurred"/> defaults to
/// creation time (UTC), not dispatch time — this captures when the business action happened.
/// </summary>
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
