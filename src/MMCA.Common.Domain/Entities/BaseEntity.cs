using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Entities;

/// <summary>
/// Base class for all domain entities. Uses <c>required init</c> for <see cref="Id"/>
/// so the identifier is immutable after construction — factory methods set the value once
/// and EF Core materializes it via the parameterless constructor.
/// </summary>
/// <typeparam name="TIdentifierType">
/// The identifier type, aliased per-entity via global usings
/// (e.g., <c>OrderIdentifierType = int</c>).
/// </typeparam>
public abstract class BaseEntity<TIdentifierType> : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    public required TIdentifierType Id { get; init; }
}
