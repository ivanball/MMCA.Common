namespace MMCA.Common.UI.Theme;

/// <summary>
/// Canonical brand color hex values: the single C# source of truth for the palette. Every hex value in
/// <see cref="MMCATheme"/> (light and dark) is one of these constants. The CSS custom properties in
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

    /// <summary>Lightened primary, used for accents and dark-mode contrast (CSS: <c>--mmca-primary-light</c>).</summary>
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

    // -- Shared chrome (identical in both palettes) --

    /// <summary>App bar and sidebar background in both palettes (CSS: <c>--mmca-sidebar-bg</c>, which reads it back as <c>--mud-palette-drawer-background</c>).</summary>
    public const string ChromeBackground = "#1A2035";

    /// <summary>App bar text on <see cref="ChromeBackground"/>.</summary>
    public const string ChromeText = "#FFFFFF";

    /// <summary>Sidebar text and icons on <see cref="ChromeBackground"/> (white at 70% alpha).</summary>
    public const string ChromeTextMuted = "#FFFFFFB3";

    // -- Light palette --

    /// <summary>Light-palette tertiary (Purple 700).</summary>
    public const string LightTertiary = "#7B1FA2";

    /// <summary>Light-palette info (Blue 700).</summary>
    public const string LightInfo = "#1976D2";

    /// <summary>Light-palette success (Green 800).</summary>
    public const string LightSuccess = "#2E7D32";

    /// <summary>Light-palette warning; the contrast rationale lives beside its use in <see cref="MMCATheme"/>.</summary>
    public const string LightWarning = "#A85D00";

    /// <summary>Label colour on <see cref="LightWarning"/>.</summary>
    public const string LightWarningContrastText = "#FFFFFF";

    /// <summary>Light-palette error (Red 800).</summary>
    public const string LightError = "#C62828";

    /// <summary>Light-palette page background.</summary>
    public const string LightBackground = "#FAFBFC";

    /// <summary>Light-palette surface (cards, fields, dialogs).</summary>
    public const string LightSurface = "#FFFFFF";

    /// <summary>Light-palette primary text.</summary>
    public const string LightTextPrimary = "#212121";

    /// <summary>Light-palette secondary text.</summary>
    public const string LightTextSecondary = "#616161";

    /// <summary>Light-palette default action (icon buttons).</summary>
    public const string LightActionDefault = "#757575";

    /// <summary>Light-palette divider (CSS: <c>--mmca-divider</c>).</summary>
    public const string LightDivider = "#E0E0E0";

    /// <summary>Light-palette light divider.</summary>
    public const string LightDividerLight = "#F5F5F5";

    // -- Dark palette --

    /// <summary>Dark-palette lightened primary (Blue 200).</summary>
    public const string DarkPrimaryLighten = "#90CAF9";

    /// <summary>Dark-palette darkened secondary (Teal 600).</summary>
    public const string DarkSecondaryDarken = "#00897B";

    /// <summary>Dark-palette lightened secondary (Teal 200).</summary>
    public const string DarkSecondaryLighten = "#80CBC4";

    /// <summary>Dark-palette tertiary (Purple 200).</summary>
    public const string DarkTertiary = "#CE93D8";

    /// <summary>Dark-palette info (Blue 400).</summary>
    public const string DarkInfo = "#42A5F5";

    /// <summary>Dark-palette success (Green 400).</summary>
    public const string DarkSuccess = "#66BB6A";

    /// <summary>Dark-palette warning (Orange 400).</summary>
    public const string DarkWarning = "#FFA726";

    /// <summary>Dark-palette error (Red A100); the contrast rationale lives beside its use in <see cref="MMCATheme"/>.</summary>
    public const string DarkError = "#FF8A80";

    /// <summary>Dark-palette page background.</summary>
    public const string DarkBackground = "#1A2027";

    /// <summary>Dark-palette surface (cards, fields, dialogs).</summary>
    public const string DarkSurface = "#27303A";

    /// <summary>Dark-palette primary text.</summary>
    public const string DarkTextPrimary = "#ECEFF1";

    /// <summary>Dark-palette secondary text.</summary>
    public const string DarkTextSecondary = "#B0BEC5";

    /// <summary>Dark-palette default action (icon buttons).</summary>
    public const string DarkActionDefault = "#B0BEC5";

    /// <summary>Dark-palette divider.</summary>
    public const string DarkDivider = "#37474F";

    /// <summary>Dark-palette light divider.</summary>
    public const string DarkDividerLight = "#2A3640";
}
