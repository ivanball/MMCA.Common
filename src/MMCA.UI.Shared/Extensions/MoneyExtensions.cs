using System.Globalization;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.UI.Shared.Extensions;

/// <summary>
/// Formatting helpers that convert <see cref="Money"/> value objects into user-friendly price strings.
/// </summary>
public static class MoneyExtensions
{
    /// <summary>Formats a single price as <c>$12.50 USD</c>.</summary>
    public static string ToDisplayString(this Money price) =>
        $"${price.Amount.ToString("N2", CultureInfo.InvariantCulture)} {price.Currency.Code}";

    /// <summary>
    /// Formats a collection of prices as a range (e.g., <c>$10.00 -- $25.00 USD</c>).
    /// When all prices are equal, a single price is displayed instead of a range.
    /// </summary>
    public static string ToDisplayRange(this IReadOnlyCollection<Money> prices)
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
