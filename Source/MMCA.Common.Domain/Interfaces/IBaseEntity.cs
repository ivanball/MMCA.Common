namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Base contract for all domain entities, providing a strongly-typed immutable identifier.
/// </summary>
/// <typeparam name="TIdentifierType">The identifier type (e.g., <see langword="int"/>, <see cref="Guid"/>).</typeparam>
public interface IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
{
    /// <summary>Gets the entity's unique identifier. Set once via <c>init</c> and immutable thereafter.</summary>
    TIdentifierType Id { get; init; }
}
