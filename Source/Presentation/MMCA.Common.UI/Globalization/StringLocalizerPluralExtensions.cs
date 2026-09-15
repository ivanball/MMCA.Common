using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Globalization;

/// <summary>
/// Plural-aware resource lookup for <see cref="IStringLocalizer"/> (ADR-027). A resource file that
/// needs a count-sensitive message declares two sibling keys beside the base key, suffixed
/// <c>.One</c> and <c>.Other</c>, and the caller asks for the base key plus a count, as in
/// <c>L.Plural("Notif.Send.SentTo", count, count)</c>.
/// The base key stays in the file as the fallback, so a resource set that has not yet been split
/// keeps rendering its single message instead of leaking a raw key name to the reader.
/// </summary>
/// <remarks>
/// Two categories (one / other) cover every culture the framework ships today
/// (<c>SupportedCultures.All</c>: English and Spanish, both of which use the Germanic one/other
/// split). A language with more CLDR categories (Polish, Russian, Arabic) needs a category selector
/// rather than more suffixes, which is a deliberate later step: the call site does not change when
/// that lands, only the key this resolver picks.
/// </remarks>
public static class StringLocalizerPluralExtensions
{
    /// <summary>Suffix of the resource key used when the count is exactly one.</summary>
    public const string OneSuffix = ".One";

    /// <summary>Suffix of the resource key used for every other count, zero included.</summary>
    public const string OtherSuffix = ".Other";

    extension(IStringLocalizer localizer)
    {
        /// <summary>
        /// Resolves the plural form of <paramref name="key"/> for <paramref name="count"/>, formatting
        /// it with <paramref name="args"/>. Returns <c>{key}.One</c> when the count is exactly one and
        /// <c>{key}.Other</c> otherwise; when the resolved plural key is missing from the resource set
        /// the lookup falls back to the base <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The base resource key, without a plural suffix.</param>
        /// <param name="count">The quantity that selects the plural category.</param>
        /// <param name="args">Format arguments; pass the count itself when the message renders it.</param>
        /// <returns>The resolved <see cref="LocalizedString"/>, never null.</returns>
        public LocalizedString Plural(string key, int count, params object[] args)
        {
            ArgumentNullException.ThrowIfNull(localizer);
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(args);

            var pluralKey = count == 1 ? key + OneSuffix : key + OtherSuffix;
            var plural = localizer[pluralKey, args];

            // ResourceNotFound is how a ResourceManager-backed localizer reports a missing key: it
            // hands back the key name itself, which is exactly what must never reach the screen.
            return plural.ResourceNotFound ? localizer[key, args] : plural;
        }
    }
}
