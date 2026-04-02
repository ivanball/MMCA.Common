using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Value object representing an ISO 4217 currency. Uses a closed set of supported currencies
/// (<see cref="All"/>) and a private constructor to prevent arbitrary instances.
/// <see cref="None"/> is an internal sentinel used by <see cref="Money.Zero()"/> to represent
/// "no currency yet" — it is never exposed to API consumers.
/// </summary>
[JsonConverter(typeof(CurrencyJsonConverter))]
public sealed record Currency : ValueObject
{
    /// <summary>Validation error returned when a currency code is null or empty.</summary>
    public static readonly Error EmptyCurrency = Error.Validation("Currency.Empty", "Currency code cannot be empty.");

    /// <summary>Validation error returned when a currency code is not in <see cref="All"/>.</summary>
    public static readonly Error InvalidCurrency = Error.Validation("Currency.Invalid", "Invalid currency code.");

    /// <summary>Sentinel value representing the absence of a currency. Used internally by <see cref="Money"/>.</summary>
    internal static readonly Currency None = new(string.Empty);

    /// <summary>United States Dollar.</summary>
    public static readonly Currency Usd = new("USD");

    /// <summary>Euro.</summary>
    public static readonly Currency Eur = new("EUR");

    private Currency(string code) => Code = code;

    /// <summary>Gets the ISO 4217 three-letter currency code.</summary>
    public string Code { get; init; }

    /// <summary>
    /// Resolves a currency from its ISO 4217 code. Only codes present in <see cref="All"/> are accepted.
    /// </summary>
    /// <param name="code">The three-letter currency code (e.g. "USD").</param>
    /// <returns>The matching <see cref="Currency"/> on success, or a validation error on failure.</returns>
    public static Result<Currency> FromCode(string code)
    {
        if (string.IsNullOrEmpty(code))
            return Result.Failure<Currency>([EmptyCurrency]);

        Currency? currency = All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (currency is null)
            return Result.Failure<Currency>([InvalidCurrency]);

        return Result.Success(currency);
    }

    /// <summary>Gets all supported currencies in the system.</summary>
    public static readonly IReadOnlyCollection<Currency> All =
    [
        Usd,
        Eur
    ];
}

/// <summary>
/// Serializes <see cref="Currency"/> as its ISO 4217 code string in JSON.
/// Deserialization validates the code against <see cref="Currency.All"/>.
/// </summary>
public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    /// <inheritdoc />
    public override Currency? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var code = reader.GetString();
        if (code is null)
            return null;

        var result = Currency.FromCode(code);
        if (result.IsFailure)
            throw new JsonException($"Invalid currency code '{code}'.");

        return result.Value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Code);
}
