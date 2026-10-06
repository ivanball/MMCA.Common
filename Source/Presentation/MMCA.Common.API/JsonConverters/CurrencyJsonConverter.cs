using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.API.JsonConverters;

/// <summary>
/// Serializes <see cref="Currency"/> as its ISO 4217 three-letter code string and deserializes
/// by validating the code through <see cref="Currency.FromCode"/>. Invalid or non-string tokens
/// throw <see cref="JsonException"/>, causing a 400 Bad Request response from the framework. The
/// empty code is the one exception: it is how the zero sentinel of <see cref="Money.Zero()"/>
/// serializes, so it reads back as that sentinel.
/// </summary>
public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    /// <inheritdoc />
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Currency must be a string.");

        string code = reader.GetString() ?? string.Empty;

        // Symmetric with Write: the zero sentinel (internal to Shared) serializes as the empty code.
        // A non-zero amount with it is refused by Money's own JSON read (IJsonOnDeserialized).
        if (code.Length == 0)
            return Money.Zero().Currency;

        var result = Currency.FromCode(code);
        if (result.IsFailure)
            throw new JsonException($"Invalid currency code: {code}");

        return result.Value!;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Code);
}
