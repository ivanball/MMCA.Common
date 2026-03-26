namespace MMCA.Common.Domain.Enums;

/// <summary>
/// Describes the state change that triggered a domain event for an entity.
/// Used in domain events to communicate whether an entity was added, updated, or removed.
/// </summary>
public enum DomainEntityState
{
    Unchanged = 0,
    Added = 1,
    Updated = 2,
    Deleted = 3
}
