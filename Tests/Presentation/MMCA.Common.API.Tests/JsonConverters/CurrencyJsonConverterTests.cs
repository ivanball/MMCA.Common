using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects.Financial;
using ApiCurrencyJsonConverter = MMCA.Common.API.JsonConverters.CurrencyJsonConverter;

namespace MMCA.Common.API.Tests.JsonConverters;

public sealed class CurrencyJsonConverterTests
{
    private readonly JsonSerializerOptions _options;

    public CurrencyJsonConverterTests()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new ApiCurrencyJsonConverter());
    }

    private static Currency ReadCurrency(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read();
        var converter = new ApiCurrencyJsonConverter();
        return converter.Read(ref reader, typeof(Currency), new JsonSerializerOptions());
    }

    [Fact]
    public void Read_ValidUsdCode_ReturnsUsdCurrency() =>
        ReadCurrency("\"USD\""u8).Should().Be(Currency.Usd);

    [Fact]
    public void Read_ValidEurCode_ReturnsEurCurrency() =>
        ReadCurrency("\"EUR\""u8).Should().Be(Currency.Eur);

    [Fact]
    public void Read_InvalidCurrencyCode_ThrowsJsonException() =>
        FluentActions.Invoking(() => ReadCurrency("\"XYZ\""u8))
            .Should().Throw<JsonException>().WithMessage("Invalid currency code*");

    // L144 (decision D6): the empty code Write emits for the zero sentinel reads back as it.
    [Fact]
    public void Read_EmptyCurrencyCode_ReturnsTheZeroSentinel() =>
        ReadCurrency("\"\""u8).Should().BeSameAs(Money.Zero().Currency);

    [Fact]
    public void Read_EmptyCode_RoundTripsTheZeroSentinel() =>
        JsonSerializer.Deserialize<Money>(JsonSerializer.Serialize(Money.Zero(), _options), _options)!.Should().Be(Money.Zero());

    [Fact]
    public void Read_NumberToken_ThrowsJsonException() =>
        FluentActions.Invoking(() => ReadCurrency("123"u8))
            .Should().Throw<JsonException>().WithMessage("Currency must be a string*");

    [Fact]
    public void Write_UsdCurrency_WritesUsdCode()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var converter = new ApiCurrencyJsonConverter();
        converter.Write(writer, Currency.Usd, _options);
        writer.Flush();

        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("\"USD\"");
    }

    [Fact]
    public void Write_EurCurrency_WritesEurCode()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var converter = new ApiCurrencyJsonConverter();
        converter.Write(writer, Currency.Eur, _options);
        writer.Flush();

        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("\"EUR\"");
    }
}
