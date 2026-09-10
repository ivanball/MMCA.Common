using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// System.Text.Json converter factory that serializes every
/// <see cref="IStronglyTypedId{TSelf, TValue}"/> as the bare primitive it wraps, so adopting a
/// wrapper is not a wire-contract change: <c>{"id":42}</c> before and after.
/// <para>
/// Registered once in <c>AddAPI</c> beside <c>EnumerationJsonConverterFactory</c>, for the same
/// reason: a <c>[JsonConverter]</c> attribute would have to be repeated on every wrapper a consumer
/// declares, and System.Text.Json does not inherit it.
/// </para>
/// <para>
/// A JSON <see langword="null"/> short-circuits before the converter runs (<c>HandleNull</c> stays
/// at its default), so a nullable identifier property round-trips as null rather than as the
/// wrapper's default.
/// </para>
/// </summary>
public sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => StronglyTypedId.IsStronglyTypedId(typeToConvert);

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = StronglyTypedId.GetValueType(typeToConvert);

        return valueType is null
            ? null
            : (JsonConverter?)Activator.CreateInstance(
                typeof(StronglyTypedIdConverter<,>).MakeGenericType(typeToConvert, valueType));
    }

    private sealed class StronglyTypedIdConverter<TSelf, TValue> : JsonConverter<TSelf>
        where TSelf : struct, IStronglyTypedId<TSelf, TValue>
        where TValue : notnull, IEquatable<TValue>
    {
        public override TSelf Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);

            return value is null ? default : TSelf.From(value);
        }

        public override void Write(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.Value, options);

        /// <summary>
        /// Writes the wrapped primitive as the property NAME when an identifier is used as a
        /// dictionary key, so <c>Dictionary&lt;OrderId, T&gt;</c> serializes exactly like
        /// <c>Dictionary&lt;int, T&gt;</c> instead of failing at runtime.
        /// </summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WritePropertyName(
                Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        /// <inheritdoc />
        public override TSelf ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => StronglyTypedId.TryParse<TSelf, TValue>(reader.GetString(), provider: null, out var result)
                ? result
                : throw new JsonException(
                    $"Cannot read a {typeof(TSelf).Name} from this property name.");
    }
}
