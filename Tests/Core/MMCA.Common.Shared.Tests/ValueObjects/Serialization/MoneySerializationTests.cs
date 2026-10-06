using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.Shared.Tests.ValueObjects.Serialization;

/// <summary>
/// Pins the round-trip contract of the private [JsonConstructor]: it is also the constructor EF Core
/// uses to materialize the owned type, so a materializer that yields a null currency must fail fast.
/// </summary>
public class MoneySerializationTests
{
    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd).Value!;

    // -- Round-trip --
    [Fact]
    public void Roundtrip_FullPayload_PreservesAmountAndCurrency()
    {
        var json = JsonSerializer.Serialize(Usd(12.5m));

        var deserialized = JsonSerializer.Deserialize<Money>(json);

        deserialized.Should().Be(Usd(12.5m));
    }

    [Fact]
    public void Serialize_WritesCurrencyAsCodeString()
    {
        var json = JsonSerializer.Serialize(Usd(3m));

        json.Should().Contain("\"Currency\":\"USD\"");
    }

    // -- Missing / null currency --
    [Fact]
    public void Deserialize_PayloadMissingCurrency_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Money>("{\"Amount\":5}"))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Deserialize_PayloadWithNullCurrency_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Money>("{\"Amount\":5,\"Currency\":null}"))
            .Should().Throw<ArgumentNullException>();

    // -- Currency.None sentinel --
    [Fact]
    public void Serialize_Zero_WritesEmptyCurrencyCode()
    {
        var json = JsonSerializer.Serialize(Money.Zero());

        json.Should().Contain("\"Currency\":\"\"");
    }

    [Fact]
    public void Deserialize_ZeroPayload_RoundTripsTheZeroSentinel()
    {
        // L144 (decision D6): the zero sentinel serializes as the empty code, and the currency
        // converter reads that code back as the sentinel, so a zero total round-trips. A non-zero
        // amount can never carry it (Money.Create refuses the sentinel).
        var json = JsonSerializer.Serialize(Money.Zero());

        JsonSerializer.Deserialize<Money>(json)!.Should().Be(Money.Zero());
    }

    [Fact]
    public void Roundtrip_ZeroWithCurrency_PreservesValue()
    {
        var json = JsonSerializer.Serialize(Money.Zero(Currency.Usd));

        var deserialized = JsonSerializer.Deserialize<Money>(json);

        deserialized.Should().Be(Money.Zero(Currency.Usd));
    }
}
