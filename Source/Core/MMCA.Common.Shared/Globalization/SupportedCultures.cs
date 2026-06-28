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
    /// Returns <see langword="true"/> when <paramref name="culture"/> is a non-empty, supported culture
    /// (matched case-insensitively against <see cref="All"/>).
    /// </summary>
    /// <param name="culture">The culture name to validate (e.g. <c>"es"</c>).</param>
    public static bool IsSupported(string? culture) =>
        !string.IsNullOrWhiteSpace(culture)
        && All.Any(c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase));
}
