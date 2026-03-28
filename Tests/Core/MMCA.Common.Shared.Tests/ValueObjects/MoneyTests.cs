using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class MoneyTests
{
    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd).Value!;

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Value!;

    // ── Create ──
    [Fact]
    public void Create_WithValidAmountAndCurrency_ReturnsSuccess()
    {
        var result = Money.Create(10m, Currency.Usd);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(10m);
        result.Value.Currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Create_WithNoneCurrency_ReturnsFailure()
    {
        var none = typeof(Currency)
            .GetField("None", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null) as Currency;

        var result = Money.Create(10m, none!);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Money.NoCurrency");
    }

    [Fact]
    public void Create_WithNegativeAmount_ReturnsSuccess()
    {
        var result = Money.Create(-5m, Currency.Usd);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsNegative.Should().BeTrue();
    }

    // ── IsNegative ──
    [Fact]
    public void IsNegative_WithNegativeAmount_ReturnsTrue()
        => Usd(-1m).IsNegative.Should().BeTrue();

    [Fact]
    public void IsNegative_WithZeroAmount_ReturnsFalse()
        => Usd(0m).IsNegative.Should().BeFalse();

    [Fact]
    public void IsNegative_WithPositiveAmount_ReturnsFalse()
        => Usd(10m).IsNegative.Should().BeFalse();

    // ── Zero ──
    [Fact]
    public void Zero_ReturnsZeroAmountWithNoneCurrency()
    {
        var zero = Money.Zero();

        zero.Amount.Should().Be(0m);
    }

    [Fact]
    public void Zero_WithCurrency_ReturnsZeroAmountWithCurrency()
    {
        var zero = Money.Zero(Currency.Usd);

        zero.Amount.Should().Be(0m);
        zero.Currency.Should().Be(Currency.Usd);
    }

    // ── IsZero ──
    [Fact]
    public void IsZero_WithZeroAmount_ReturnsTrue()
        => Money.Zero(Currency.Usd).IsZero().Should().BeTrue();

    [Fact]
    public void IsZero_WithNonZeroAmount_ReturnsFalse()
        => Usd(5m).IsZero().Should().BeFalse();

    // ── Addition ──
    [Fact]
    public void Addition_SameCurrency_AddAmounts()
    {
        var result = Usd(10m) + Usd(5m);

        result.Amount.Should().Be(15m);
        result.Currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Addition_FirstIsNoneCurrency_ReturnsSecond()
    {
        var result = Money.Zero() + Usd(5m);

        result.Amount.Should().Be(5m);
        result.Currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Addition_SecondIsNoneCurrency_ReturnsFirst()
    {
        var result = Usd(5m) + Money.Zero();

        result.Amount.Should().Be(5m);
        result.Currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Addition_DifferentCurrencies_ThrowsInvalidOperationException()
    {
        var act = () => Usd(10m) + Eur(5m);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Currencies have to be equal*");
    }

    // ── Multiplication ──
    [Fact]
    public void Multiplication_MultipliesAmountByQuantity()
    {
        var result = Usd(10m) * 3;

        result.Amount.Should().Be(30m);
        result.Currency.Should().Be(Currency.Usd);
    }

    [Fact]
    public void Multiplication_ByZero_ReturnsZeroAmount()
    {
        var result = Usd(10m) * 0;

        result.Amount.Should().Be(0m);
    }

    // ── Static methods ──
    [Fact]
    public void Add_SameCurrency_ReturnsSuccess()
    {
        var result = Money.Add(Usd(3m), Usd(7m));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(10m);
    }

    [Fact]
    public void Add_DifferentCurrencies_ReturnsFailure()
    {
        var result = Money.Add(Usd(3m), Eur(7m));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Money.CurrencyMismatch");
    }

    [Fact]
    public void Multiply_DelegatesToOperator()
    {
        var result = Money.Multiply(Usd(4m), 5);

        result.Amount.Should().Be(20m);
    }

    // ── Equality ──
    [Fact]
    public void Equality_SameAmountAndCurrency_AreEqual()
        => Usd(10m).Should().Be(Usd(10m));

    [Fact]
    public void Equality_DifferentAmount_AreNotEqual()
        => Usd(10m).Should().NotBe(Usd(20m));

    [Fact]
    public void Equality_DifferentCurrency_AreNotEqual()
        => Usd(10m).Should().NotBe(Eur(10m));
}
