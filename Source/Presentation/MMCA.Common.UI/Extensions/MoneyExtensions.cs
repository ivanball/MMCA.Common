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
        /// number format of <paramref name="culture"/>, so a Spanish reader gets
        /// <c>$1.234,56 USD</c> where an English one gets <c>$1,234.56 USD</c>.
        /// </summary>
        /// <param name="culture">
        /// The culture whose number format is used. Left unset (the normal call in a Blazor page) it
        /// resolves to <see cref="CultureInfo.CurrentCulture"/>, which the request's culture provider
        /// has already set to the reader's. Pass one explicitly when rendering off the reader's
        /// thread: a background job, an export, a test.
        /// </param>
        public string ToDisplayString(CultureInfo? culture = null) =>
            FormatGroup(price.Amount, price.Amount, price.Currency.Code, culture);
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
        /// <param name="culture">
        /// The culture whose number format is used, resolving to
        /// <see cref="CultureInfo.CurrentCulture"/> when unset, exactly as a single price does. The
        /// two must not drift: a one-element range and the price it holds have to read the same.
        /// </param>
        public string ToDisplayRange(CultureInfo? culture = null)
        {
            if (prices.Count == 0)
            {
                return string.Empty;
            }

            var resolved = culture ?? CultureInfo.CurrentCulture;

            // GroupBy preserves first-appearance order, so a single-currency collection (every
            // collection in practice today) renders exactly one group and is unchanged.
            var groups = prices
                .GroupBy(p => p.Currency.Code, StringComparer.Ordinal)
                .Select(g => FormatGroup(g.Min(p => p.Amount), g.Max(p => p.Amount), g.Key, resolved));

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
    /// <param name="min">The lower bound of the group.</param>
    /// <param name="max">The upper bound of the group; equal to <paramref name="min"/> for a single price.</param>
    /// <param name="code">The currency code shared by the group.</param>
    /// <param name="culture">The culture to format under, or <see langword="null"/> for the current one.</param>
    private static string FormatGroup(decimal min, decimal max, string code, CultureInfo? culture)
    {
        var resolved = culture ?? CultureInfo.CurrentCulture;
        var symbol = Symbol(code);
        var body = min == max
            ? $"{symbol}{min.ToString("N2", resolved)}"
            : $"{symbol}{min.ToString("N2", resolved)} - {symbol}{max.ToString("N2", resolved)}";

        return string.IsNullOrEmpty(code) ? body : $"{body} {code}";
    }
}
