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
}
