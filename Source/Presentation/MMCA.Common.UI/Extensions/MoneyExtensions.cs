using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MMCA.Common.Shared.ValueObjects.Financial;

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
        /// <summary>Formats a single price as <c>$12.50 USD</c>, using the symbol of its own currency.</summary>
        public string ToDisplayString() =>
            FormatGroup(price.Amount, price.Amount, price.Currency.Code);
    }

    extension(IReadOnlyCollection<Money> prices)
    {
        /// <summary>
        /// Formats a collection of prices as a range (e.g., <c>$10.00 - $25.00 USD</c>).
        /// When all prices are equal, a single price is displayed instead of a range.
        /// Prices are grouped by currency, so a mixed collection renders one range per currency,
        /// each with its own symbol, instead of collapsing unrelated amounts under whichever
        /// currency happened to appear first.
        /// </summary>
        public string ToDisplayRange()
        {
            if (prices.Count == 0)
            {
                return string.Empty;
            }

            // GroupBy preserves first-appearance order, so a single-currency collection (every
            // collection in practice today) renders exactly one group and is unchanged.
            var groups = prices
                .GroupBy(p => p.Currency.Code, StringComparer.Ordinal)
                .Select(g => FormatGroup(g.Min(p => p.Amount), g.Max(p => p.Amount), g.Key));

            return string.Join(", ", groups);
        }
    }

    /// <summary>
    /// Resolves the display symbol for a currency code. Unknown codes and the empty code of the
    /// <c>Currency.None</c> sentinel behind <c>Money.Zero()</c> render without a symbol rather than
    /// falsely claiming dollars.
    /// </summary>
    private static string Symbol(string code) => code switch
    {
        "USD" => "$",
        "EUR" => "\u20AC", // euro sign, escaped to keep this source file ASCII-only
        _ => string.Empty,
    };

    /// <summary>
    /// Formats one currency's amounts, as a single price when the bounds are equal and as a range
    /// otherwise. The trailing code is omitted for the empty sentinel code.
    /// </summary>
    private static string FormatGroup(decimal min, decimal max, string code)
    {
        var symbol = Symbol(code);
        var body = min == max
            ? $"{symbol}{min.ToString("N2", CultureInfo.InvariantCulture)}"
            : $"{symbol}{min.ToString("N2", CultureInfo.InvariantCulture)} - {symbol}{max.ToString("N2", CultureInfo.InvariantCulture)}";

        return string.IsNullOrEmpty(code) ? body : $"{body} {code}";
    }
}
