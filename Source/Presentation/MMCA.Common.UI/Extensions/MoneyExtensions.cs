using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.UI.Extensions;

/// <summary>
/// Formatting helpers that convert <see cref="Money"/> value objects into user-friendly price strings.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class MoneyExtensions
{
    extension(Money price)
    {
        /// <summary>Formats a single price as <c>$12.50 USD</c>.</summary>
        public string ToDisplayString() =>
            $"${price.Amount.ToString("N2", CultureInfo.InvariantCulture)} {price.Currency.Code}";
    }

    extension(IReadOnlyCollection<Money> prices)
    {
        /// <summary>
        /// Formats a collection of prices as a range (e.g., <c>$10.00 -- $25.00 USD</c>).
        /// When all prices are equal, a single price is displayed instead of a range.
        /// </summary>
        public string ToDisplayRange()
        {
            if (prices.Count == 0)
            {
                return string.Empty;
            }

            var min = prices.Min(p => p.Amount);
            var max = prices.Max(p => p.Amount);
            var currency = prices.First().Currency.Code;

            return min == max
                ? $"${min.ToString("N2", CultureInfo.InvariantCulture)} {currency}"
                : $"${min.ToString("N2", CultureInfo.InvariantCulture)} — ${max.ToString("N2", CultureInfo.InvariantCulture)} {currency}";
        }
    }
}
