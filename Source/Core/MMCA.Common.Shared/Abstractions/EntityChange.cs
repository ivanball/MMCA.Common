using System.Runtime.Serialization;

namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// One entry of a change feed (a changed-since read): an upsert carrying the row's normal DTO, or a
/// tombstone for a deleted row carrying no item. A page of the feed is a
/// <see cref="KeysetCollectionResult{T}"/> of these.
/// </summary>
/// <typeparam name="TEntityDTO">The DTO an upsert carries.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <remarks>
/// A client applies entries idempotently by <see cref="Id"/> (plus the DTO's row version when it has
/// one): page with the same <c>since</c> until the next cursor is null, then keep the largest
/// <see cref="ChangedOn"/> as the next <c>since</c>. The bound is inclusive, so boundary rows come again.
/// </remarks>
[DataContract]
public sealed record EntityChange<TEntityDTO, TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Gets the identifier of the changed row.</summary>
    [DataMember(Order = 1)]
    public required TIdentifierType Id { get; init; }

    /// <summary>Gets a value indicating whether the row was deleted (a tombstone with no <see cref="Item"/>).</summary>
    [DataMember(Order = 2)]
    public required bool IsDeleted { get; init; }

    /// <summary>Gets the UTC instant of the change: the row's last modification, else its creation.</summary>
    [DataMember(Order = 3)]
    public required DateTime ChangedOn { get; init; }

    /// <summary>Gets the row's DTO for an upsert, or <see langword="null"/> for a tombstone.</summary>
    [DataMember(Order = 4)]
    public TEntityDTO? Item { get; init; }
}
