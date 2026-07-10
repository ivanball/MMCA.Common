using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Serialization;

/// <summary>
/// System.Text.Json converter factory for <see cref="Result"/> and <see cref="Result{T}"/>.
/// The Result types deliberately keep internal constructors and get-only properties, which
/// default reflection-based deserialization cannot rehydrate. This factory makes them
/// round-trippable (required by the distributed query cache, which serializes cached
/// handler results to Redis) by writing a compact <c>{"value": ..., "errors": [...]}</c>
/// shape and reconstructing through the public factory methods.
/// </summary>
public sealed class ResultJsonConverterFactory : JsonConverterFactory
{
    private const string ValuePropertyName = "value";
    private const string ErrorsPropertyName = "errors";

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(Result)
        || typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Result<>);

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(Result))
            return new ResultConverter();

        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter?)Activator.CreateInstance(typeof(ResultConverter<>).MakeGenericType(valueType));
    }

    private sealed class ResultConverter : JsonConverter<Result>
    {
        public override Result Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<Error>? errors = null;

            ReadObject(ref reader, options, (ref r, propertyName) =>
            {
                if (string.Equals(propertyName, ErrorsPropertyName, StringComparison.OrdinalIgnoreCase))
                    errors = JsonSerializer.Deserialize<List<Error>>(ref r, options);
                else
                    r.Skip();
            });

            return errors is { Count: > 0 } ? Result.Failure(errors) : Result.Success();
        }

        public override void Write(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            WriteErrors(writer, value, options);
            writer.WriteEndObject();
        }
    }

    private sealed class ResultConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            T? value = default;
            List<Error>? errors = null;

            ReadObject(ref reader, options, (ref r, propertyName) =>
            {
                if (string.Equals(propertyName, ValuePropertyName, StringComparison.OrdinalIgnoreCase))
                    value = JsonSerializer.Deserialize<T>(ref r, options);
                else if (string.Equals(propertyName, ErrorsPropertyName, StringComparison.OrdinalIgnoreCase))
                    errors = JsonSerializer.Deserialize<List<Error>>(ref r, options);
                else
                    r.Skip();
            });

            return errors is { Count: > 0 } ? Result.Failure<T>(errors) : Result.Success(value!);
        }

        public override void Write(Utf8JsonWriter writer, Result<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (value.IsSuccess)
            {
                writer.WritePropertyName(ValuePropertyName);
                JsonSerializer.Serialize(writer, value.Value, options);
            }

            WriteErrors(writer, value, options);
            writer.WriteEndObject();
        }
    }

    private delegate void PropertyReader(ref Utf8JsonReader reader, string propertyName);

    /// <summary>Walks the properties of the current JSON object, delegating each value to <paramref name="readProperty"/>.</summary>
    private static void ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options, PropertyReader readProperty)
    {
        _ = options;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object for a Result payload.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a property name in a Result payload.");

            var propertyName = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                throw new JsonException("Truncated Result payload.");

            readProperty(ref reader, propertyName);
        }

        throw new JsonException("Truncated Result payload.");
    }

    private static void WriteErrors(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
    {
        if (value.IsSuccess)
            return;

        writer.WritePropertyName(ErrorsPropertyName);
        JsonSerializer.Serialize(writer, value.Errors, options);
    }
}
