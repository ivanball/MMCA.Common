using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects.Financial;
using MMCA.Common.UI.Extensions;

namespace MMCA.Common.UI.Tests.Extensions;

[SuppressMessage(
    "Globalization",
    "CA1304:Specify CultureInfo",
    Justification = "The culture-less overload is the subject under test: these facts exist to pin what the ambient culture produces, so routing them through the explicit overload would assert nothing.")]
public class MoneyExtensionsTests
{
    /// <summary>
    /// Pins <see cref="CultureInfo.CurrentCulture"/> for the duration of one test and restores the
    /// ambient value afterwards. The formatters read the current culture, so a fact that asserts a
    /// literal string has to say which culture produced it instead of inheriting the build agent's.
    /// </summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        private CultureScope(string name) =>
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public static CultureScope Use(string name) => new(name);

        public void Dispose() => CultureInfo.CurrentCulture = _original;
    }

    private static Money CreateMoney(decimal amount) =>
        Money.Create(amount, Currency.Usd).Value!;

    private static Money CreateEur(decimal amount) =>
        Money.Create(amount, Currency.Eur).Value!;

    // -- ToDisplayString --
    [Fact]
    public void ToDisplayString_FormatsAmountWithCurrencyCode()
    {
        using var culture = CultureScope.Use("en-US");
        var money = CreateMoney(12.50m);
        money.ToDisplayString().Should().Be("$12.50 USD");
    }

    [Fact]
    public void ToDisplayString_ZeroAmount_FormatsCorrectly()
    {
        using var culture = CultureScope.Use("en-US");
        var money = CreateMoney(0m);
        money.ToDisplayString().Should().Be("$0.00 USD");
    }

    [Fact]
    public void ToDisplayString_LargeAmount_IncludesThousandsSeparator()
    {
        using var culture = CultureScope.Use("en-US");
        var money = CreateMoney(1234.56m);
        money.ToDisplayString().Should().Be("$1,234.56 USD");
    }

    [Fact]
    public void ToDisplayString_EurCurrency_ShowsEuroSymbolNotDollar()
    {
        using var culture = CultureScope.Use("en-US");
        var money = CreateEur(9.99m);
        money.ToDisplayString().Should().Be("€9.99 EUR");
    }

    [Fact]
    // Money.Zero() carries the internal Currency.None sentinel (empty code). Rendering it as a
    // dollar amount was wrong, and the empty code used to leave a trailing space.
    public void ToDisplayString_ZeroSentinel_RendersWithoutSymbolOrDanglingCode()
    {
        using var culture = CultureScope.Use("en-US");
        Money.Zero().ToDisplayString().Should().Be("0.00");
    }

    [Fact]
    // The separators swap round in Spanish: a reader whose request carries es-ES must not be shown
    // an English number. Only the digits move; the symbol and the code belong to the money.
    public void ToDisplayString_SpanishCulture_SwapsGroupAndDecimalSeparators()
    {
        using var culture = CultureScope.Use("es-ES");
        CreateMoney(1234.56m).ToDisplayString().Should().Be("$1.234,56 USD");
    }

    [Fact]
    public void ToDisplayString_ExplicitCulture_IgnoresTheAmbientCulture()
    {
        using var culture = CultureScope.Use("en-US");
        CreateMoney(1234.56m)
            .ToDisplayString(CultureInfo.GetCultureInfo("es-ES"))
            .Should()
            .Be("$1.234,56 USD");
    }

    [Fact]
    public void ToDisplayString_ExplicitEnglishCulture_IsStableUnderASpanishAmbientCulture()
    {
        using var culture = CultureScope.Use("es-ES");
        CreateMoney(1234.56m)
            .ToDisplayString(CultureInfo.GetCultureInfo("en-US"))
            .Should()
            .Be("$1,234.56 USD");
    }

    // -- ToDisplayRange --
    [Fact]
    public void ToDisplayRange_EmptyCollection_ReturnsEmptyString()
    {
        List<Money> prices = [];
        prices.ToDisplayRange().Should().BeEmpty();
    }

    [Fact]
    public void ToDisplayRange_SinglePrice_ShowsSingleValue()
    {
        using var culture = CultureScope.Use("en-US");
        List<Money> prices = [CreateMoney(25.00m)];
        prices.ToDisplayRange().Should().Be("$25.00 USD");
    }

    [Fact]
    public void ToDisplayRange_EqualPrices_ShowsSingleValue()
    {
        using var culture = CultureScope.Use("en-US");
        List<Money> prices = [CreateMoney(10.00m), CreateMoney(10.00m)];
        prices.ToDisplayRange().Should().Be("$10.00 USD");
    }

    [Fact]
    public void ToDisplayRange_DifferentPrices_ShowsMinMaxRange()
    {
        using var culture = CultureScope.Use("en-US");
        List<Money> prices = [CreateMoney(10.00m), CreateMoney(25.00m), CreateMoney(15.00m)];
        prices.ToDisplayRange().Should().Be("$10.00 - $25.00 USD");
    }

    [Fact]
    public void ToDisplayRange_MixedCurrencies_GroupsPerCurrencyInsteadOfMixingThem()
    {
        using var culture = CultureScope.Use("en-US");

        // Before the fix min/max spanned every entry while the code came from the first one, so
        // this rendered "$8.00 - $25.00 USD": a range that never existed, in the wrong currency.
        List<Money> prices = [CreateMoney(10.00m), CreateMoney(25.00m), CreateEur(8.00m)];
        prices.ToDisplayRange().Should().Be("$10.00 - $25.00 USD, €8.00 EUR");
    }

    [Fact]
    public void ToDisplayRange_MixedCurrencies_PreservesFirstAppearanceOrder()
    {
        using var culture = CultureScope.Use("en-US");
        List<Money> prices = [CreateEur(8.00m), CreateMoney(10.00m), CreateEur(12.00m)];
        prices.ToDisplayRange().Should().Be("€8.00 - €12.00 EUR, $10.00 USD");
    }

    [Fact]
    // A range follows the reader's culture exactly as a single price does; the two must not drift.
    public void ToDisplayRange_SpanishCulture_FormatsBothBoundsInThatCulture()
    {
        using var culture = CultureScope.Use("es-ES");
        List<Money> prices = [CreateMoney(1234.56m), CreateMoney(2345.67m)];
        prices.ToDisplayRange().Should().Be("$1.234,56 - $2.345,67 USD");
    }
}
