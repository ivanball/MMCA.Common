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
    /// Returns <see langword="true"/> when <paramref name="culture"/> is the pseudo-localization locale
    /// (<see cref="PseudoLocale"/>), matched case-insensitively.
    /// </summary>
    /// <param name="culture">The culture name to test (e.g. <c>CultureInfo.CurrentUICulture.Name</c>).</param>
    public static bool IsPseudoLocale(string? culture) =>
        string.Equals(culture, PseudoLocale, StringComparison.OrdinalIgnoreCase);
}
