using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Immutable value object representing a monetary amount paired with a <see cref="ValueObjects.Currency"/>.
/// Arithmetic operations enforce currency compatibility — adding two different currencies produces a
/// <see cref="CurrencyMismatch"/> error. The <see cref="Currency.None"/> sentinel acts as an identity
/// element for addition, allowing <see cref="Zero()"/> to be used as an accumulator seed regardless
/// of the target currency.
/// </summary>
/// <remarks>
/// Configured as an EF owned type via <c>OwnsOne</c> in entity configurations.
/// </remarks>
[DataContract]
public sealed record Money : ValueObject
{
    /// <summary>Validation error returned when <see cref="Create"/> is called with <see cref="Currency.None"/>.</summary>
    public static readonly Error NoCurrency = Error.Validation("Money.NoCurrency", "Currency is required.");

    /// <summary>Validation error returned when an arithmetic operation combines two different real currencies.</summary>
    public static readonly Error CurrencyMismatch = Error.Validation("Money.CurrencyMismatch", "Currencies have to be equal.");

    /// <summary>Gets the monetary amount. May be negative (e.g. for refunds or adjustments).</summary>
    [DataMember(Order = 1)]
    public decimal Amount { get; init; }

    /// <summary>Gets the currency for this monetary value.</summary>
    [DataMember(Order = 2)]
    public Currency Currency { get; init; }

    /// <summary>Gets a value indicating whether the amount is less than zero.</summary>
    public bool IsNegative => Amount < 0;

    [JsonConstructor]
    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>
    /// Creates a <see cref="Money"/> instance with the specified amount and currency.
    /// Rejects <see cref="Currency.None"/> since external callers must always specify a real currency.
    /// </summary>
    /// <param name="amount">The monetary amount.</param>
    /// <param name="currency">The currency (must not be <see cref="Currency.None"/>).</param>
    /// <returns>A success result with the money value, or a validation error if currency is missing.</returns>
    public static Result<Money> Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (currency == Currency.None)
            return Result.Failure<Money>(NoCurrency);

        return Result.Success(new Money(amount, currency));
    }

    /// <summary>
    /// Adds two money values. Throws <see cref="InvalidOperationException"/> on currency mismatch.
    /// Prefer <see cref="Add"/> for Result-based error handling.
    /// </summary>
    /// <param name="first">The left operand.</param>
    /// <param name="second">The right operand.</param>
    /// <returns>A new <see cref="Money"/> with the summed amount.</returns>
    public static Money operator +(Money first, Money second)
    {
        var result = Add(first, second);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException(result.Errors[0].Message);
    }

    /// <summary>Multiplies the monetary amount by a quantity.</summary>
    /// <param name="first">The money value.</param>
    /// <param name="quantity">The multiplier.</param>
    /// <returns>A new <see cref="Money"/> with the multiplied amount.</returns>
    public static Money operator *(Money first, int quantity)
        => new(first.Amount * quantity, first.Currency);

    /// <summary>
    /// Safely adds two money values with Result-based error handling.
    /// Returns a <see cref="CurrencyMismatch"/> error if currencies differ
    /// (unless one side is <see cref="Currency.None"/>).
    /// </summary>
    /// <param name="first">The first money value.</param>
    /// <param name="second">The second money value.</param>
    /// <returns>A success result with the sum, or a failure with <see cref="CurrencyMismatch"/>.</returns>
    public static Result<Money> Add(Money first, Money second)
    {
        if (first.Currency != Currency.None && second.Currency != Currency.None
            && first.Currency != second.Currency)
        {
            return Result.Failure<Money>(CurrencyMismatch
                .WithSource(nameof(Add))
                .WithTarget($"{first.Currency.Code} + {second.Currency.Code}"));
        }

        return Result.Success(AddUnchecked(first, second));
    }

    /// <summary>Multiplies the monetary amount by a quantity. Equivalent to the <c>*</c> operator.</summary>
    /// <param name="first">The money value.</param>
    /// <param name="quantity">The multiplier.</param>
    /// <returns>A new <see cref="Money"/> with the multiplied amount.</returns>
    public static Money Multiply(Money first, int quantity) => first * quantity;

    /// <summary>
    /// Adds two money values without currency validation.
    /// Treats <see cref="Currency.None"/> as an identity element — if one side has no currency,
    /// the other side's currency is preserved.
    /// </summary>
    private static Money AddUnchecked(Money first, Money second)
    {
        if (first.Currency == Currency.None)
            return second;
        if (second.Currency == Currency.None)
            return first;
        return new(first.Amount + second.Amount, first.Currency);
    }

    /// <summary>Creates a zero-amount money with <see cref="Currency.None"/>. Useful as an accumulator seed.</summary>
    /// <returns>A zero <see cref="Money"/> with no currency.</returns>
    public static Money Zero() => new(0, Currency.None);

    /// <summary>Creates a zero-amount money with the specified currency.</summary>
    /// <param name="currency">The currency for the zero value.</param>
    /// <returns>A zero <see cref="Money"/> in the given currency.</returns>
    public static Money Zero(Currency currency) => new(0, currency);

    /// <summary>
    /// Creates a <see cref="Money"/> without currency validation. Internal — exposed to test
    /// assemblies via <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>.
    /// </summary>
    internal static Money CreateUnsafe(decimal amount, Currency currency) => new(amount, currency);

    /// <summary>Determines whether this money value is zero in its currency.</summary>
    /// <returns><see langword="true"/> if the amount equals zero for this currency.</returns>
    public bool IsZero() => this == Zero(Currency);
}
