namespace MMCA.Common.Shared.DTOs;

/// <summary>
/// Lightweight DTO for dropdown/autocomplete lookups containing only an identifier and display name.
/// Used by query handlers that return simplified reference data.
/// </summary>
/// <typeparam name="TIdentifierType">The type of the entity identifier.</typeparam>
public record class BaseLookup<TIdentifierType> : IBaseDTO<TIdentifierType>
        where TIdentifierType : notnull
{
    /// <inheritdoc />
    public required TIdentifierType Id { get; init; }

    /// <summary>Gets or initializes the display name for the lookup item.</summary>
    public required string Name { get; init; }
}
