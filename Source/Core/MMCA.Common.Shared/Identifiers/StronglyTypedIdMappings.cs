using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// The two conversions an object mapper needs when a DTO exposes the primitive while the entity
/// holds the wrapper (ADR-115).
/// <para>
/// Mapperly maps a wrapper to itself with no help at all, which is the normal case here: a DTO
/// implements <c>IBaseDTO&lt;TIdentifierType&gt;</c> over the SAME identifier type its entity uses,
/// so the generated mapper is a plain assignment. This class exists for the other case, a contract
/// that deliberately keeps primitives on the wire, and it is reached without a Mapperly attribute:
/// <code>
/// [Mapper]
/// [UseStaticMapper(typeof(StronglyTypedIdMappings&lt;OrderId, int&gt;))]
/// public sealed partial class OrderDTOMapper
/// {
///     public partial OrderDTO MapToDTO(Order entity);
/// }
/// </code>
/// Mapperly discovers the two public static methods by signature, so the framework ships plain
/// methods and takes no dependency on <c>Riok.Mapperly.Abstractions</c> in the Shared layer.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
/// <typeparam name="TValue">The wrapped primitive.</typeparam>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The static members ARE the contract: Mapperly's [UseStaticMapper(typeof(StronglyTypedIdMappings<OrderId, int>))] discovers public static methods on a closed generic type by signature. An instance API would not be reachable from a generated mapper.")]
public static class StronglyTypedIdMappings<TSelf, TValue>
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    /// <summary>Unwraps an identifier for a contract that carries the primitive.</summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The wrapped primitive.</returns>
    public static TValue ToValue(TSelf identifier) => identifier.Value;

    /// <summary>Wraps a primitive read from a contract.</summary>
    /// <param name="value">The primitive.</param>
    /// <returns>The identifier carrying it.</returns>
    public static TSelf ToIdentifier(TValue value) => TSelf.From(value);

    /// <summary>Unwraps an optional identifier, keeping "not set" as <see langword="null"/>.</summary>
    /// <param name="identifier">The identifier, or <see langword="null"/>.</param>
    /// <returns>The wrapped primitive, or <see langword="null"/>.</returns>
    public static TValue? ToNullableValue(TSelf? identifier) => identifier is null ? default : identifier.Value.Value;

    /// <summary>Wraps an optional primitive, keeping "not set" as <see langword="null"/>.</summary>
    /// <param name="value">The primitive, or <see langword="null"/>.</param>
    /// <returns>The identifier, or <see langword="null"/>.</returns>
    public static TSelf? ToNullableIdentifier(TValue? value) => value is null ? null : TSelf.From(value);
}
