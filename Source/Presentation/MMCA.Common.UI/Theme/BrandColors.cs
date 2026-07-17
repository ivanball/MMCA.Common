namespace MMCA.Common.UI.Theme;

/// <summary>
/// Canonical brand color hex values — the single C# source of truth for the palette, referenced by
/// <see cref="MMCATheme"/> for both light and dark variants. The CSS custom properties in
/// <c>wwwroot/app.css</c> (<c>--mmca-primary</c>, <c>--mmca-primary-dark</c>) must mirror these
/// (C# cannot read CSS at build time); <c>BrandColorTokenTests</c> in MMCA.Common.UI.Tests asserts
/// they stay in sync so the duplication can't silently drift.
/// </summary>
public static class BrandColors
{
    /// <summary>Primary brand blue (CSS: <c>--mmca-primary</c>).</summary>
    public const string Primary = "#1565C0";

    /// <summary>Darkened primary (CSS: <c>--mmca-primary-dark</c>).</summary>
    public const string PrimaryDark = "#0D47A1";

    /// <summary>Lightened primary, used for accents and dark-mode contrast.</summary>
    public const string PrimaryLight = "#42A5F5";

    /// <summary>
    /// Secondary brand teal (CSS: <c>--mmca-secondary</c>). Teal 700: <c>Color.Secondary</c> renders
    /// muted helper text, and #00796B holds ~5.3:1 contrast on light surfaces (the Teal 600 #00897B
    /// it replaced was ~4.0:1, under the WCAG 2.1 AA 4.5:1 floor).
    /// </summary>
    public const string Secondary = "#00796B";

    /// <summary>Darkened secondary (CSS: <c>--mmca-secondary-dark</c>).</summary>
    public const string SecondaryDark = "#00695C";

    /// <summary>Lightened secondary, used for accents and dark-mode contrast.</summary>
    public const string SecondaryLight = "#4DB6AC";
}
