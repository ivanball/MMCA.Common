using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.Shared.Tests.ValueObjects.Financial;

public class CurrencyTests
{
    // ── FromCode ──
    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void FromCode_WithASupportedCode_ReturnsThatCurrency(string code)
    {
        Currency expected = code switch
        {
            "USD" => Currency.Usd,
            "EUR" => Currency.Eur,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown currency."),
        };

        var result = Currency.FromCode(code);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Fact]
    public void FromCode_WithEmptyString_ReturnsEmptyCurrencyError()
    {
        var result = Currency.FromCode(string.Empty);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Currency.Empty");
    }

    [Fact]
    public void FromCode_WithInvalidCode_ReturnsInvalidCurrencyError()
    {
        var result = Currency.FromCode("GBP");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Currency.Invalid");
    }

    // ── All collection ──
    [Fact]
    public void All_ContainsUsdAndEur()
    {
        Currency.All.Should().HaveCount(2);
        Currency.All.Should().Contain(Currency.Usd);
        Currency.All.Should().Contain(Currency.Eur);
    }

    // ── Equality ──
    [Fact]
    public void Equality_SameCode_AreEqual()
        => Currency.Usd.Should().Be(Currency.Usd);

    [Fact]
    public void Equality_DifferentCode_AreNotEqual()
        => Currency.Usd.Should().NotBe(Currency.Eur);
}
