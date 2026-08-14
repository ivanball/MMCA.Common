using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Abstract base for a smart enumeration: a closed set of named, integer-valued members declared on
/// the derived type as <c>public static readonly</c> fields. Unlike a CLR enum, a member is a real
/// object, so behavior (policies, rates, display rules) can hang off it instead of living in a
/// switch statement somewhere else.
/// </summary>
/// <typeparam name="TEnumeration">
/// The concrete enumeration type. The self-referencing constraint keeps <see cref="All"/>,
/// <see cref="FromValue"/> and <see cref="FromName"/> strongly typed per enumeration, so each closed
/// type gets its own lookup tables.
/// </typeparam>
/// <remarks>
/// Lives in <c>MMCA.Common.Shared</c> so it stays dependency-free and usable from Blazor WASM/UI as
/// well as Domain. It deliberately does NOT derive from <see cref="ValueObject"/>: the
/// <c>ValueObjectsAreImmutableSealedInShared</c> fitness rule forces every <see cref="ValueObject"/>
/// derivative to be a sealed record living in the Shared layer, which would forbid the static-member
/// idiom this type exists for. <c>RoleValue</c> is the shipped precedent for the same trade-off.
/// <para>
/// Members are discovered by reflection over the <c>public static readonly</c> fields declared on
/// <typeparamref name="TEnumeration"/> itself, on first use, and then frozen. Two members sharing a
/// <see cref="Value"/> or a <see cref="Name"/> (case-insensitively) are a declaration bug and fail
/// fast with an <see cref="ArgumentException"/> the first time the enumeration is touched.
/// </para>
/// <para>
/// Intentionally does not implement <see cref="IEquatable{T}"/> (S4035: an unsealed
/// <c>IEquatable&lt;T&gt;</c> breaks the equality contract for subclasses). Equality is provided via
/// the <see cref="object.Equals(object?)"/> override below, type-guarded so two members are equal
/// only when they are the same concrete type with the same value; a sealed derived type may add a
/// strongly-typed <c>IEquatable&lt;TSelf&gt;</c> and <c>==</c>/<c>!=</c> operators on top.
/// </para>
/// <para>
/// <b>Usage:</b> repeat the <see cref="JsonConverterAttribute"/> on the concrete type.
/// System.Text.Json reads that attribute off the type it is converting without walking base types,
/// so the declaration on this base class only covers a member typed as the base itself. The
/// alternative, for a host that would rather not annotate each type, is registering
/// <see cref="EnumerationJsonConverterFactory"/> once in <c>JsonSerializerOptions.Converters</c>
/// (the shipped <c>CurrencyJsonConverter</c> registration in <c>AddAPI</c> is the precedent).
/// <code>
/// [JsonConverter(typeof(EnumerationJsonConverterFactory))]
/// public sealed class Priority : Enumeration&lt;Priority&gt;
/// {
///     public static readonly Priority Low = new(1, "Low");
///     public static readonly Priority High = new(2, "High");
///
///     private Priority(int value, string name)
///         : base(value, name)
///     {
///     }
/// }
/// </code>
/// </para>
/// </remarks>
[DataContract]
[JsonConverter(typeof(EnumerationJsonConverterFactory))]
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The self-referencing generic constraint is the point: All/FromValue/FromName are per-enumeration lookups over the closed type's own members, and calls read as Priority.FromValue(2) with no type argument written by hand. A non-generic sibling would return the base type and force every call site to cast.")]
public abstract class Enumeration<TEnumeration>
    where TEnumeration : Enumeration<TEnumeration>
{
    private static readonly Lazy<ReadOnlyCollection<TEnumeration>> MembersLazy = new(DiscoverMembers);

    private static readonly Lazy<FrozenDictionary<int, TEnumeration>> ByValueLazy =
        new(() => MembersLazy.Value.ToFrozenDictionary(member => member.Value));

    private static readonly Lazy<FrozenDictionary<string, TEnumeration>> ByNameLazy =
        new(() => MembersLazy.Value.ToFrozenDictionary(
            member => member.Name,
            StringComparer.OrdinalIgnoreCase));

    /// <summary>Initializes a new instance of the <see cref="Enumeration{TEnumeration}"/> class.</summary>
    /// <param name="value">The member's stable integer value, used as the persisted representation.</param>
    /// <param name="name">The member's canonical name, used as the serialized representation.</param>
    protected Enumeration(int value, string name)
    {
        Value = value;
        Name = name;
    }

    /// <summary>Gets the member's canonical name. This is the JSON and display representation.</summary>
    [DataMember(Order = 1)]
    public string Name { get; }

    /// <summary>Gets the member's stable integer value. This is the persisted representation.</summary>
    [DataMember(Order = 2)]
    public int Value { get; }

    /// <summary>
    /// Gets every member declared on <typeparamref name="TEnumeration"/>, ordered by
    /// <see cref="Value"/>. Built once on first use and cached for the lifetime of the closed type.
    /// </summary>
    public static IReadOnlyCollection<TEnumeration> All => MembersLazy.Value;

    /// <summary>
    /// Resolves the member carrying <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The member value to resolve.</param>
    /// <returns>
    /// The matching member on success, or an invariant failure with code
    /// <c>Enumeration.UnknownValue</c> when no member declares that value.
    /// </returns>
    public static Result<TEnumeration> FromValue(int value)
    {
        if (ByValueLazy.Value.TryGetValue(value, out TEnumeration? member))
            return Result.Success(member);

        return Result.Failure<TEnumeration>(Error.Invariant(
            code: "Enumeration.UnknownValue",
            message: $"'{value.ToString(CultureInfo.InvariantCulture)}' is not a known {typeof(TEnumeration).Name} value.",
            source: nameof(FromValue),
            target: nameof(value)));
    }

    /// <summary>
    /// Resolves the member carrying <paramref name="name"/>. The match is case-insensitive, matching
    /// the comparison <c>RoleValue</c> uses for the same kind of closed string set.
    /// </summary>
    /// <param name="name">The member name to resolve.</param>
    /// <returns>
    /// The matching member on success, or an invariant failure with code
    /// <c>Enumeration.UnknownName</c> when no member declares that name.
    /// </returns>
    public static Result<TEnumeration> FromName(string name)
    {
        if (ByNameLazy.Value.TryGetValue(name ?? string.Empty, out TEnumeration? member))
            return Result.Success(member);

        return Result.Failure<TEnumeration>(Error.Invariant(
            code: "Enumeration.UnknownName",
            message: $"'{name}' is not a known {typeof(TEnumeration).Name} name.",
            source: nameof(FromName),
            target: nameof(name)));
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is Enumeration<TEnumeration> other
        && GetType() == other.GetType()
        && Value == other.Value;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Value);

    /// <summary>
    /// Reflects over the <c>public static readonly</c> fields declared directly on
    /// <typeparamref name="TEnumeration"/> and materializes them as the member set. Inherited fields
    /// are excluded, so a derived hierarchy never inherits another type's members.
    /// </summary>
    private static ReadOnlyCollection<TEnumeration> DiscoverMembers() =>
        typeof(TEnumeration)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(memberField => memberField.IsInitOnly
                && typeof(TEnumeration).IsAssignableFrom(memberField.FieldType))
            .Select(memberField => memberField.GetValue(null))
            .OfType<TEnumeration>()
            .OrderBy(member => member.Value)
            .ToArray()
            .AsReadOnly();
}

/// <summary>
/// Serializes every <see cref="Enumeration{TEnumeration}"/> member as its <c>Name</c> string in JSON.
/// Deserialization resolves the name through <see cref="Enumeration{TEnumeration}.FromName"/>; a
/// non-string token (number, object, array, boolean) and an unknown name both throw
/// <see cref="JsonException"/>, matching <c>CurrencyJsonConverter</c> so non-MVC paths (cache,
/// outbox, integration events, typed HttpClient calls) fail the same way MVC model binding does.
/// </summary>
/// <remarks>
/// Declared on <see cref="Enumeration{TEnumeration}"/> via <see cref="JsonConverterAttribute"/>, but
/// System.Text.Json resolves that attribute off the type being converted without walking base types,
/// so a concrete enumeration either repeats the attribute or the host registers this factory once in
/// <see cref="JsonSerializerOptions.Converters"/>. Both routes select the same converter.
/// <para>
/// <see cref="JsonConverter{T}.HandleNull"/> is left at its default (<see langword="false"/>), so
/// the serializer short-circuits a JSON null token before the converter runs and nullable members
/// keep deserializing to <see langword="null"/>.
/// </para>
/// </remarks>
public sealed class EnumerationJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        GetEnumerationArgument(typeToConvert) == typeToConvert;

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(
            typeof(EnumerationConverter<>).MakeGenericType(typeToConvert));

    /// <summary>
    /// Walks the base chain of <paramref name="typeToConvert"/> looking for
    /// <c>Enumeration&lt;T&gt;</c> and returns its <c>T</c>. The caller compares that argument with
    /// the type itself: only the self-referencing closed type (the one the constraint is written
    /// for) is converted, so a type deriving further from a concrete enumeration is left to the
    /// default converter rather than silently serialized as its base.
    /// </summary>
    private static Type? GetEnumerationArgument(Type? typeToConvert)
    {
        for (Type? type = typeToConvert; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Enumeration<>))
                return type.GetGenericArguments()[0];
        }

        return null;
    }

    private sealed class EnumerationConverter<TEnumeration> : JsonConverter<TEnumeration>
        where TEnumeration : Enumeration<TEnumeration>
    {
        public override TEnumeration? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException($"{typeof(TEnumeration).Name} must be a string.");

            string name = reader.GetString() ?? string.Empty;

            var result = Enumeration<TEnumeration>.FromName(name);
            if (result.IsFailure)
                throw new JsonException($"'{name}' is not a known {typeof(TEnumeration).Name} name.");

            return result.Value;
        }

        public override void Write(Utf8JsonWriter writer, TEnumeration value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Name);
    }
}
