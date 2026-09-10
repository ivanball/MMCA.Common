using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// Opt-in contract for a strongly typed identifier: a wrapper struct that carries one primitive
/// <typeparamref name="TValue"/> but is a distinct CLR type, so two identifiers backed by the same
/// primitive can no longer be swapped at a call site without a compiler error.
/// <para>
/// The framework's default identifier model is still the primitive alias
/// (<c>global using OrderIdentifierType = int</c>, ADR-048/ADR-085). This interface adds a second,
/// opt-in style on the same footing smart enumerations sit on (ADR-104): the framework ships the
/// capability, wires it into JSON, EF Core, route binding, filtering and OpenAPI, and adopts it
/// nowhere.
/// </para>
/// <para>
/// <b>The canonical declaration is two lines</b> (everything else is inherited):
/// <code>
/// public readonly record struct OrderId(int Value) : IStronglyTypedId&lt;OrderId, int&gt;
/// {
///     public static OrderId From(int value) =&gt; new(value);
/// }
/// </code>
/// The positional record struct supplies <c>Value</c>, structural equality, <c>GetHashCode</c> and
/// a <c>ToString</c>; this interface supplies <see cref="IParsable{TSelf}"/> through the two default
/// implementations below, so the type binds from a route segment or a query string with no extra
/// code. <see cref="StronglyTypedIdJsonConverterFactory"/> serializes it as the bare primitive.
/// </para>
/// <para>
/// <b>One consequence of the two lines being two lines:</b> the <see cref="IParsable{TSelf}"/> legs
/// are EXPLICIT default implementations, which is the only form a default implementation of an
/// inherited static abstract member can take. So <c>OrderId.Parse("42", null)</c> does not compile
/// against the wrapper directly; parse through the helper
/// (<c>StronglyTypedId.Parse&lt;OrderId, int&gt;(text, provider)</c>) or from any generic context
/// constrained to <see cref="IParsable{T}"/>. Every framework boundary that matters already reaches
/// it through the interface: minimal-API binding and
/// <see cref="StronglyTypedIdTypeConverter{TSelf, TValue}"/>, which is what MVC route and query
/// binding uses. A wrapper that would rather expose the pair publicly adds two forwarding lines of
/// its own.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The wrapper struct itself (the self-referencing type argument).</typeparam>
/// <typeparam name="TValue">
/// The wrapped primitive. <see langword="int"/>, <see langword="long"/>, <see cref="Guid"/> and
/// <see langword="string"/> are supported end to end; any other type that implements
/// <see cref="IParsable{T}"/> parses too (see <see cref="StronglyTypedId"/>).
/// </typeparam>
/// <remarks>
/// Deliberately NOT a <c>ValueObject</c> derivative. The
/// <c>ValueObjectsAreImmutableSealedInShared</c> fitness rule governs reference-type value objects
/// with a <c>Create</c> factory returning <c>Result</c>; an identifier wrapper is a struct with no
/// validation to fail, so it is covered by its own rule
/// (<c>StronglyTypedIdsAreReadonlyRecordStructs</c>) instead. This is the same reasoning
/// <c>Enumeration&lt;T&gt;</c> records for staying outside that rule.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1033:Interface methods should be callable by child types",
    Justification = "A default interface implementation of an inherited member can only be written as an explicit implementation; there is no non-explicit form. The point of these two is that a wrapper struct never writes them: IParsable is reached through the interface, which is what MVC binding and the framework helpers hold.")]
public interface IStronglyTypedId<TSelf, TValue> : IParsable<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    /// <summary>Gets the wrapped primitive value.</summary>
    TValue Value { get; }

    /// <summary>
    /// Wraps <paramref name="value"/> in a new identifier. The one member an implementation writes.
    /// </summary>
    /// <param name="value">The primitive to wrap.</param>
    /// <returns>The identifier carrying <paramref name="value"/>.</returns>
    static abstract TSelf From(TValue value);

    /// <summary>
    /// Parses a route segment or query value into an identifier, delegating to
    /// <see cref="StronglyTypedId.Parse{TSelf, TValue}(string, IFormatProvider?)"/>. Supplied as a
    /// default implementation so a wrapper never writes it.
    /// </summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">The format provider; <see langword="null"/> means invariant.</param>
    /// <returns>The parsed identifier.</returns>
    static TSelf IParsable<TSelf>.Parse(string s, IFormatProvider? provider)
        => StronglyTypedId.Parse<TSelf, TValue>(s, provider);

    /// <summary>
    /// Tries to parse a route segment or query value into an identifier, delegating to
    /// <see cref="StronglyTypedId.TryParse{TSelf, TValue}(string?, IFormatProvider?, out TSelf)"/>.
    /// Supplied as a default implementation so a wrapper never writes it.
    /// </summary>
    /// <param name="s">The text to parse, or <see langword="null"/>.</param>
    /// <param name="provider">The format provider; <see langword="null"/> means invariant.</param>
    /// <param name="result">The parsed identifier, or the default when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> parsed.</returns>
    static bool IParsable<TSelf>.TryParse(
        string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out TSelf result)
        => StronglyTypedId.TryParse<TSelf, TValue>(s, provider, out result);
}
