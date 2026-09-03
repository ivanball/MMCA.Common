using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

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
    public void Deserialize_ZeroPayload_ThrowsJsonException()
    {
        // The Currency.None sentinel is an in-memory/EF concept: its empty code is not a valid
        // ISO 4217 code, so the currency converter rejects it on the way back in. Documented
        // contract, unchanged by the constructor guard.
        var json = JsonSerializer.Serialize(Money.Zero());

        FluentActions.Invoking(() => JsonSerializer.Deserialize<Money>(json))
            .Should().Throw<JsonException>();
    }

    [Fact]
    public void Roundtrip_ZeroWithCurrency_PreservesValue()
    {
        var json = JsonSerializer.Serialize(Money.Zero(Currency.Usd));

        var deserialized = JsonSerializer.Deserialize<Money>(json);

        deserialized.Should().Be(Money.Zero(Currency.Usd));
    }
}
