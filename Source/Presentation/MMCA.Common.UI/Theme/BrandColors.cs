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
}
