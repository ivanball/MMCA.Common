using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.API.JsonConverters;

/// <summary>
/// Serializes <see cref="Currency"/> as its ISO 4217 three-letter code string and deserializes
/// by validating the code through <see cref="Currency.FromCode"/>. Invalid or non-string tokens
/// throw <see cref="JsonException"/>, causing a 400 Bad Request response from the framework.
/// </summary>
public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    /// <inheritdoc />
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Currency must be a string.");

        string code = reader.GetString() ?? string.Empty;
        var result = Currency.FromCode(code);
        if (result.IsFailure)
            throw new JsonException($"Invalid currency code: {code}");

        return result.Value!;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Code);
}
