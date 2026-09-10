using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// Parsing and reflection helpers shared by every <see cref="IStronglyTypedId{TSelf, TValue}"/>
/// implementation and by the framework plumbing that has to recognize one at runtime (the JSON
/// converter factory, the EF Core convention, the filter strategy and the OpenAPI schema
/// transformer).
/// <para>
/// A wrapper struct never calls the parse helpers directly: they back the two default
/// <see cref="IParsable{TSelf}"/> implementations on the interface, which is why the canonical
/// declaration is one <c>From</c> method and nothing else.
/// </para>
/// </summary>
public static class StronglyTypedId
{
    /// <summary>
    /// Caches the wrapped value type per identifier type, and a null entry for every type that is
    /// not an identifier. Bounded by the number of CLR types the process loads, so unlike a
    /// query-string keyed cache it cannot be grown by a caller.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Type?> ValueTypeCache = new();

    /// <summary>
    /// Parses <paramref name="s"/> into <typeparamref name="TSelf"/>, throwing
    /// <see cref="FormatException"/> when the text is not a valid
    /// <typeparamref name="TValue"/>. This is the <see cref="IParsable{TSelf}.Parse"/> leg, so it
    /// throws by contract rather than returning a <c>Result</c>.
    /// </summary>
    /// <typeparam name="TSelf">The identifier type.</typeparam>
    /// <typeparam name="TValue">The wrapped primitive.</typeparam>
    /// <param name="s">The text to parse.</param>
    /// <param name="provider">The format provider; <see langword="null"/> means invariant culture.</param>
    /// <returns>The parsed identifier.</returns>
    /// <exception cref="FormatException">The text is not a valid <typeparamref name="TValue"/>.</exception>
    public static TSelf Parse<TSelf, TValue>(string s, IFormatProvider? provider)
        where TSelf : struct, IStronglyTypedId<TSelf, TValue>
        where TValue : notnull, IEquatable<TValue>
    {
        ArgumentNullException.ThrowIfNull(s);

        return TryParse<TSelf, TValue>(s, provider, out var result)
            ? result
            : throw new FormatException(
                $"'{s}' is not a valid {typeof(TSelf).Name}.");
    }

    /// <summary>
    /// Tries to parse <paramref name="s"/> into <typeparamref name="TSelf"/>. Returns
    /// <see langword="false"/> for a null or unparseable input, and for a
    /// <typeparamref name="TValue"/> the framework cannot parse (see <see cref="CanParseValues{TValue}"/>).
    /// </summary>
    /// <typeparam name="TSelf">The identifier type.</typeparam>
    /// <typeparam name="TValue">The wrapped primitive.</typeparam>
    /// <param name="s">The text to parse, or <see langword="null"/>.</param>
    /// <param name="provider">The format provider; <see langword="null"/> means invariant culture.</param>
    /// <param name="result">The parsed identifier, or the default when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> parsed.</returns>
    public static bool TryParse<TSelf, TValue>(string? s, IFormatProvider? provider, out TSelf result)
        where TSelf : struct, IStronglyTypedId<TSelf, TValue>
        where TValue : notnull, IEquatable<TValue>
    {
        var parser = StronglyTypedIdValueParser<TValue>.Instance;
        if (parser is not null && parser(s, provider ?? CultureInfo.InvariantCulture, out var value))
        {
            result = TSelf.From(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Whether the framework can turn text into a <typeparamref name="TValue"/>. True for
    /// <see langword="string"/> and for every <see cref="IParsable{T}"/> primitive, which covers
    /// <see langword="int"/>, <see langword="long"/> and <see cref="Guid"/>.
    /// </summary>
    /// <typeparam name="TValue">The wrapped primitive.</typeparam>
    /// <returns><see langword="true"/> when parsing is supported.</returns>
    public static bool CanParseValues<TValue>()
        where TValue : notnull
        => StronglyTypedIdValueParser<TValue>.Instance is not null;

    /// <summary>
    /// Returns the primitive a strongly typed identifier wraps, or <see langword="null"/> when
    /// <paramref name="type"/> is not one. Only the self-referencing closed type is recognized, the
    /// same guard <c>EnumerationJsonConverterFactory</c> applies, so a type that merely happens to
    /// implement the interface for a different self argument is left alone.
    /// </summary>
    /// <param name="type">The candidate type.</param>
    /// <returns>The wrapped primitive type, or <see langword="null"/>.</returns>
    public static Type? GetValueType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ValueTypeCache.GetOrAdd(type, ResolveValueType);
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a strongly typed identifier.
    /// </summary>
    /// <param name="type">The candidate type.</param>
    /// <returns><see langword="true"/> when it implements <see cref="IStronglyTypedId{TSelf, TValue}"/> for itself.</returns>
    public static bool IsStronglyTypedId(Type type) => GetValueType(type) is not null;

    /// <summary>
    /// Unwraps a nullable identifier type before asking <see cref="GetValueType"/>, so a caller
    /// inspecting an <c>OrderId?</c> property gets the same answer as one inspecting <c>OrderId</c>.
    /// </summary>
    /// <param name="type">The candidate type, possibly <see cref="Nullable{T}"/>.</param>
    /// <param name="identifierType">The non-nullable identifier type when the answer is yes.</param>
    /// <param name="valueType">The wrapped primitive when the answer is yes.</param>
    /// <returns><see langword="true"/> when the type is, or wraps, a strongly typed identifier.</returns>
    public static bool TryDescribe(
        Type type,
        [NotNullWhen(true)] out Type? identifierType,
        [NotNullWhen(true)] out Type? valueType)
    {
        ArgumentNullException.ThrowIfNull(type);

        identifierType = Nullable.GetUnderlyingType(type) ?? type;
        valueType = GetValueType(identifierType);

        if (valueType is not null)
            return true;

        identifierType = null;
        return false;
    }

    private static Type? ResolveValueType(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(IStronglyTypedId<,>))
                continue;

            var arguments = contract.GetGenericArguments();
            if (arguments[0] == type)
                return arguments[1];
        }

        return null;
    }
}

/// <summary>
/// Turns text into a wrapped primitive. Matches the shape of
/// <see cref="IParsable{TSelf}.TryParse(string?, IFormatProvider?, out TSelf)"/> so a closed
/// <c>IParsable</c> implementation can be bound to it directly.
/// </summary>
/// <typeparam name="TValue">The wrapped primitive.</typeparam>
/// <param name="s">The text to parse, or <see langword="null"/>.</param>
/// <param name="provider">The format provider.</param>
/// <param name="value">The parsed primitive, or the default when parsing fails.</param>
/// <returns><see langword="true"/> when <paramref name="s"/> parsed.</returns>
internal delegate bool StronglyTypedIdValueParserDelegate<TValue>(
    string? s,
    IFormatProvider? provider,
    out TValue value)
    where TValue : notnull;

/// <summary>
/// Per-primitive parser cache. The delegate is built once for each closed
/// <typeparamref name="TValue"/> in a static initializer, so the reflection cost is paid at most
/// once per wrapped primitive in the process rather than per request.
/// </summary>
/// <typeparam name="TValue">The wrapped primitive.</typeparam>
internal static class StronglyTypedIdValueParser<TValue>
    where TValue : notnull
{
    /// <summary>
    /// The parser for <typeparamref name="TValue"/>, or <see langword="null"/> when the primitive is
    /// neither <see langword="string"/> nor <see cref="IParsable{T}"/>.
    /// </summary>
    internal static readonly StronglyTypedIdValueParserDelegate<TValue>? Instance = Build();

    [SuppressMessage(
        "Minor Code Smell",
        "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "The private method being bound is declared on THIS type, a few lines below, and is bound once per closed generic to avoid a per-request reflection cost. Nothing outside the class is reached and no caller supplies the name.")]
    private static StronglyTypedIdValueParserDelegate<TValue>? Build()
    {
        // string is the one supported primitive that does NOT implement IParsable<string>, so it
        // takes the identity path: the route segment IS the value.
        if (typeof(TValue) == typeof(string))
            return ParseString;

        if (!typeof(IParsable<>).MakeGenericType(typeof(TValue)).IsAssignableFrom(typeof(TValue)))
            return null;

        var method = typeof(StronglyTypedIdValueParser<TValue>)
            .GetMethod(nameof(ParseParsable), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TValue));

        return method.CreateDelegate<StronglyTypedIdValueParserDelegate<TValue>>();
    }

    private static bool ParseString(string? s, IFormatProvider? provider, out TValue value)
    {
        value = (TValue)(object)(s ?? string.Empty);
        return s is not null;
    }

    private static bool ParseParsable<TParsable>(string? s, IFormatProvider? provider, out TParsable value)
        where TParsable : IParsable<TParsable>
    {
        if (s is not null && TParsable.TryParse(s, provider, out var parsed) && parsed is not null)
        {
            value = parsed;
            return true;
        }

        value = default!;
        return false;
    }
}
