using System.Text;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// Pure pseudo-localization transform (ADR-027 §8). Accents every letter, pads the text to simulate
/// the ~40% expansion many real translations need, and wraps the result in a bracket sentinel.
/// Composite-format placeholders (<c>{0}</c>, <c>{name}</c>) are left byte-identical so the string can
/// still be formatted with arguments. Applied at runtime by <see cref="PseudoStringLocalizer"/> only
/// when the current UI culture is <see cref="Shared.Globalization.SupportedCultures.PseudoLocale"/>.
/// </summary>
/// <remarks>
/// The transform deliberately changes both content and width so three classes of i18n defect become
/// visible in one pass: <list type="bullet">
///   <item><description>hard-coded (non-resource) strings stay plain ASCII, unmissable beside accented text;</description></item>
///   <item><description>fixed-width UI truncates the padded text, exposing layouts that cannot grow;</description></item>
///   <item><description>concatenated fragments each gain their own sentinel, revealing the seams.</description></item>
/// </list>
/// </remarks>
public static class PseudoLocalizer
{
    private const string OpenSentinel = "[!! ";
    private const string CloseSentinel = " !!]";
    private const char CombiningAcute = '́';

    /// <summary>
    /// Returns the pseudo-localized form of <paramref name="value"/>. Empty/null input is returned
    /// unchanged. Text inside <c>{ }</c> placeholders is preserved verbatim.
    /// </summary>
    public static string Transform(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + value.Length / 2 + OpenSentinel.Length + CloseSentinel.Length + 8);
        builder.Append(OpenSentinel);

        var insidePlaceholder = false;
        var letters = 0;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '{':
                    insidePlaceholder = true;
                    builder.Append(ch);
                    break;
                case '}':
                    insidePlaceholder = false;
                    builder.Append(ch);
                    break;
                default:
                    builder.Append(ch);
                    if (!insidePlaceholder && char.IsLetter(ch))
                    {
                        // Accent the letter in place (combining acute) so every letter is visibly altered
                        // while the base glyph stays readable.
                        builder.Append(CombiningAcute);
                        letters++;
                    }

                    break;
            }
        }

        // Visible width padding (~40% of the letter count) so fixed-width layouts that cannot grow truncate.
        var padLength = Math.Max(1, letters * 2 / 5);
        builder.Append(' ');
        builder.Append('~', padLength);
        builder.Append(CloseSentinel);
        return builder.ToString();
    }
}
