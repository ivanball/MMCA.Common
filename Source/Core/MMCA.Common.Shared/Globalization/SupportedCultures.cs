namespace MMCA.Common.Shared.Globalization;

/// <summary>
/// The framework-wide allowlist of supported UI cultures (ADR-027). Adding a locale means adding a
/// <c>.&lt;culture&gt;.resx</c> sibling set and one entry here — no other infrastructure change.
/// Referenced by the UI/service hosts' <c>UseRequestLocalization</c>, the culture switcher, and the
/// Identity <c>User.PreferredCulture</c> guard so they cannot drift apart.
/// </summary>
public static class SupportedCultures
{
    /// <summary>The default culture, used when no cookie/profile/Accept-Language preference resolves.</summary>
    public const string Default = "en-US";

    /// <summary>
    /// All supported cultures, default first. Both the request-localization options and the culture
    /// switcher iterate this list.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Default, "es"];

    /// <summary>
    /// The Windows-standard pseudo-localization locale (ADR-027 §8). Deliberately <b>not</b> part of
    /// <see cref="All"/>, so the translation-completeness fitness gate does not demand a
    /// <c>.qps-Ploc.resx</c> sibling for it. Wired into request localization, the culture-switch
    /// endpoint, and the culture switcher in <b>Development only</b>, where it runtime-transforms every
    /// resolved resource string (accents + padding + bracket sentinel) to surface hard-coded strings,
    /// truncation, and string concatenation without translating anything.
    /// </summary>
    public const string PseudoLocale = "qps-Ploc";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="culture"/> is a non-empty, supported culture
    /// (matched case-insensitively against <see cref="All"/>).
    /// </summary>
    /// <param name="culture">The culture name to validate (e.g. <c>"es"</c>).</param>
    public static bool IsSupported(string? culture) =>
        !string.IsNullOrWhiteSpace(culture)
        && All.Any(c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves an arbitrary culture name to the closest supported culture: an exact
    /// <see cref="All"/> match wins, then a language match (so <c>"es-MX"</c> resolves to <c>"es"</c>),
    /// otherwise <see cref="Default"/>. Never returns <see cref="PseudoLocale"/>.
    /// <para>
    /// Web heads get this fallback for free from request localization's <c>Accept-Language</c>
    /// matching. It exists here for heads that resolve their own culture and have no request pipeline
    /// to lean on (MAUI Blazor Hybrid, which resolves against the device locale), so the two paths
    /// cannot drift.
    /// </para>
    /// </summary>
    /// <param name="culture">Any culture name, including a specific one not on the allowlist.</param>
    public static string ResolveClosest(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return Default;
        }

        var exact = All.FirstOrDefault(c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var language = LanguageOf(culture);
        var byLanguage = All.FirstOrDefault(
            c => string.Equals(LanguageOf(c), language, StringComparison.OrdinalIgnoreCase));

        return byLanguage ?? Default;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="culture"/> is the pseudo-localization locale
    /// (<see cref="PseudoLocale"/>), matched case-insensitively.
    /// </summary>
    /// <param name="culture">The culture name to test (e.g. <c>CultureInfo.CurrentUICulture.Name</c>).</param>
    public static bool IsPseudoLocale(string? culture) =>
        string.Equals(culture, PseudoLocale, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The language subtag of a culture name (<c>"es-MX"</c> to <c>"es"</c>), without allocating for a
    /// neutral culture that is already just its language.
    /// </summary>
    private static string LanguageOf(string culture)
    {
        var separator = culture.IndexOf('-', StringComparison.Ordinal);
        return separator < 0 ? culture : culture[..separator];
    }
}
