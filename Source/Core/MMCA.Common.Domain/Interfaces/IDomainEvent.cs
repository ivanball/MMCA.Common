namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Marker interface for domain events. Domain events represent something meaningful
/// that happened within the aggregate boundary and are dispatched after successful persistence.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Gets the UTC timestamp of when the domain action occurred (not when it was dispatched).</summary>
    DateTime DateOccurred { get; }
}
