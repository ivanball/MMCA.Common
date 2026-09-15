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
        /// <summary>
        /// Formats a single price as <c>$12.50 USD</c>, using the symbol of its own currency and the
        /// number format of <see cref="CultureInfo.CurrentCulture"/>, so a Spanish request renders
        /// <c>$1.234,56 USD</c> where an English one renders <c>$1,234.56 USD</c>.
        /// </summary>
        public string ToDisplayString() =>
            FormatGroup(price.Amount, price.Amount, price.Currency.Code, CultureInfo.CurrentCulture);

        /// <summary>
        /// Formats a single price using an explicitly supplied culture, for callers that render on a
        /// thread whose culture is not the reader's (background jobs, exports, tests).
        /// </summary>
        /// <param name="culture">The culture whose number format is used.</param>
        public string ToDisplayString(CultureInfo culture) =>
            FormatGroup(price.Amount, price.Amount, price.Currency.Code, culture);
    }

    extension(IReadOnlyCollection<Money> prices)
    {
        /// <summary>
        /// Formats a collection of prices as a range (e.g., <c>$10.00 - $25.00 USD</c>).
        /// When all prices are equal, a single price is displayed instead of a range.
        /// Prices are grouped by currency, so a mixed collection renders one range per currency,
        /// each with its own symbol, instead of collapsing unrelated amounts under whichever
        /// currency happened to appear first. Amounts follow <see cref="CultureInfo.CurrentCulture"/>,
        /// exactly as a single price does.
        /// </summary>
        public string ToDisplayRange()
        {
            if (prices.Count == 0)
            {
                return string.Empty;
            }

            var culture = CultureInfo.CurrentCulture;

            // GroupBy preserves first-appearance order, so a single-currency collection (every
            // collection in practice today) renders exactly one group and is unchanged.
            var groups = prices
                .GroupBy(p => p.Currency.Code, StringComparer.Ordinal)
                .Select(g => FormatGroup(g.Min(p => p.Amount), g.Max(p => p.Amount), g.Key, culture));

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
    /// otherwise. The trailing code is omitted for the empty sentinel code. The currency symbol and
    /// the trailing code come from the money itself; only the digit grouping and the decimal
    /// separator follow <paramref name="culture"/>, so a USD price stays USD in every locale.
    /// </summary>
    private static string FormatGroup(decimal min, decimal max, string code, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var symbol = Symbol(code);
        var body = min == max
            ? $"{symbol}{min.ToString("N2", culture)}"
            : $"{symbol}{min.ToString("N2", culture)} - {symbol}{max.ToString("N2", culture)}";

        return string.IsNullOrEmpty(code) ? body : $"{body} {code}";
    }
}
