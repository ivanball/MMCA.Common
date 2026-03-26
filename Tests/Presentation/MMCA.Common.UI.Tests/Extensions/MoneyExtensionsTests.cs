using FluentAssertions;
using MMCA.Common.Shared.ValueObjects;
using MMCA.Common.UI.Extensions;

namespace MMCA.Common.UI.Tests.Extensions;

public class MoneyExtensionsTests
{
    private static Money CreateMoney(decimal amount) =>
        Money.Create(amount, Currency.Usd).Value!;

    private static Money CreateEur(decimal amount) =>
        Money.Create(amount, Currency.Eur).Value!;

    // ── ToDisplayString ──
    [Fact]
    public void ToDisplayString_FormatsAmountWithCurrencyCode()
    {
        var money = CreateMoney(12.50m);
        money.ToDisplayString().Should().Be("$12.50 USD");
    }

    [Fact]
    public void ToDisplayString_ZeroAmount_FormatsCorrectly()
    {
        var money = CreateMoney(0m);
        money.ToDisplayString().Should().Be("$0.00 USD");
    }

    [Fact]
    public void ToDisplayString_LargeAmount_IncludesThousandsSeparator()
    {
        var money = CreateMoney(1234.56m);
        money.ToDisplayString().Should().Be("$1,234.56 USD");
    }

    [Fact]
    public void ToDisplayString_EurCurrency_ShowsEurCode()
    {
        var money = CreateEur(9.99m);
        money.ToDisplayString().Should().Be("$9.99 EUR");
    }

    // ── ToDisplayRange ──
    [Fact]
    public void ToDisplayRange_EmptyCollection_ReturnsEmptyString()
    {
        List<Money> prices = [];
        prices.ToDisplayRange().Should().BeEmpty();
    }

    [Fact]
    public void ToDisplayRange_SinglePrice_ShowsSingleValue()
    {
        List<Money> prices = [CreateMoney(25.00m)];
        prices.ToDisplayRange().Should().Be("$25.00 USD");
    }

    [Fact]
    public void ToDisplayRange_EqualPrices_ShowsSingleValue()
    {
        List<Money> prices = [CreateMoney(10.00m), CreateMoney(10.00m)];
        prices.ToDisplayRange().Should().Be("$10.00 USD");
    }

    [Fact]
    public void ToDisplayRange_DifferentPrices_ShowsMinMaxRange()
    {
        List<Money> prices = [CreateMoney(10.00m), CreateMoney(25.00m), CreateMoney(15.00m)];
        prices.ToDisplayRange().Should().Be("$10.00 — $25.00 USD");
    }
}
