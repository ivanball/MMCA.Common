namespace MMCA.Common.Shared.DTOs;

/// <summary>
/// Marker interface for all DTOs that carry an entity identifier. Enables generic
/// query services (<c>IEntityQueryService</c>) and controller base classes to work
/// with any DTO type uniformly.
/// </summary>
/// <typeparam name="TIdentifierType">The type of the entity identifier (e.g. <see langword="int"/>, <see cref="System.Guid"/>).</typeparam>
public interface IBaseDTO<TIdentifierType>
        where TIdentifierType : notnull
{
    /// <summary>Gets or initializes the entity identifier.</summary>
    TIdentifierType Id { get; init; }
}
