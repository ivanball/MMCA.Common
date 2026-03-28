using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class CurrencyJsonConverterTests
{
    // ── Serialize ──
    [Fact]
    public void Write_SerializesCurrencyAsCodeString()
    {
        var json = JsonSerializer.Serialize(Currency.Usd);
        json.Should().Be("\"USD\"");
    }

    [Fact]
    public void Write_Eur_SerializesAsEurString()
    {
        var json = JsonSerializer.Serialize(Currency.Eur);
        json.Should().Be("\"EUR\"");
    }

    // ── Deserialize ──
    [Fact]
    public void Read_ValidUsdCode_DeserializesToUsd()
    {
        var currency = JsonSerializer.Deserialize<Currency>("\"USD\"");
        currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Read_ValidEurCode_DeserializesToEur()
    {
        var currency = JsonSerializer.Deserialize<Currency>("\"EUR\"");
        currency.Should().Be(Currency.Eur);
    }

    [Fact]
    public void Read_InvalidCode_ThrowsJsonException() =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Currency>("\"XYZ\""))
            .Should().Throw<JsonException>();

    [Fact]
    public void Read_NullValue_ReturnsNull()
    {
        var currency = JsonSerializer.Deserialize<Currency>("null");
        currency.Should().BeNull();
    }

    [Fact]
    public void Read_NullValue_IsDistinctFromCurrencyNone()
    {
        var deserialized = JsonSerializer.Deserialize<Currency>("null");

        // null should be a true null reference, not a Currency.None sentinel
        deserialized.Should().BeNull();
        Currency.Usd.Should().NotBeNull();
    }

    // ── Roundtrip ──
    [Fact]
    public void Roundtrip_PreservesValue()
    {
        var json = JsonSerializer.Serialize(Currency.Usd);
        var deserialized = JsonSerializer.Deserialize<Currency>(json);

        deserialized.Should().Be(Currency.Usd);
    }
}
