using MudBlazor;

namespace MMCA.Common.UI.Theme;

/// <summary>
/// Application-wide MudBlazor theme defining the brand palette, typography, and layout properties.
/// Applied via <c>MudThemeProvider</c> in the root layout.
/// </summary>
public static class MMCATheme
{
    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            // Brand palette sourced from BrandColors (the single C# source of truth). The CSS
            // tokens --mmca-primary / --mmca-primary-dark in wwwroot/app.css must mirror these;
            // BrandColorTokenTests asserts the two stay in sync.
            Primary = BrandColors.Primary,
            PrimaryDarken = BrandColors.PrimaryDark,
            PrimaryLighten = BrandColors.PrimaryLight,
            // Teal 700 (was Teal 600 #00897B). Color.Secondary is used for muted helper text
            // (mud-secondary-text); on light surfaces #00897B is ~4.0:1, just under the WCAG 2.1 AA
            // 4.5:1 floor for normal text. #00796B is ~5.3:1 and keeps the brand teal aesthetic.
            Secondary = "#00796B",
            SecondaryDarken = "#00695C",
            SecondaryLighten = "#4DB6AC",
            Tertiary = "#7B1FA2",
            Info = "#1976D2",
            Success = "#2E7D32",
            Warning = "#F57F17",
            // MudBlazor's default warning contrast text is white, which is ~2.65:1 on #F57F17 and
            // fails the WCAG 2.1 AA 4.5:1 floor on every filled Warning chip/button (caught by the
            // gated admin-order-list axe scan on a "Pending Payment" chip). Dark text is ~7.9:1 and
            // is the standard Material treatment on amber. Info/Success/Error pass with white.
            WarningContrastText = "#212121",
            Error = "#C62828",
            AppbarBackground = "#1A2035",
            AppbarText = "#FFFFFF",
            Background = "#FAFBFC",
            Surface = "#FFFFFF",
            DrawerBackground = "#1A2035",
            DrawerText = "#FFFFFFB3",
            DrawerIcon = "#FFFFFFB3",
            TextPrimary = "#212121",
            TextSecondary = "#616161",
            ActionDefault = "#757575",
            Divider = "#E0E0E0",
            DividerLight = "#F5F5F5",
        },
        PaletteDark = new PaletteDark
        {
            // Brand palette tuned for dark surfaces — primary lightened for contrast on dark
            // backgrounds. Enables dark mode via MudThemeProvider's IsDarkMode (rubric §20).
            Primary = BrandColors.PrimaryLight,
            PrimaryDarken = BrandColors.Primary,
            PrimaryLighten = "#90CAF9",
            Secondary = "#4DB6AC",
            SecondaryDarken = "#00897B",
            SecondaryLighten = "#80CBC4",
            Tertiary = "#CE93D8",
            Info = "#42A5F5",
            Success = "#66BB6A",
            Warning = "#FFA726",
            // Same fix as the light palette: white on #FFA726 is ~2.0:1; dark text is ~10.8:1.
            WarningContrastText = "rgba(0,0,0,0.87)",
            Error = "#EF5350",
            AppbarBackground = "#1A2035",
            AppbarText = "#FFFFFF",
            Background = "#1A2027",
            Surface = "#27303A",
            DrawerBackground = "#1A2035",
            DrawerText = "#FFFFFFB3",
            DrawerIcon = "#FFFFFFB3",
            TextPrimary = "#ECEFF1",
            TextSecondary = "#B0BEC5",
            ActionDefault = "#B0BEC5",
            Divider = "#37474F",
            DividerLight = "#2A3640",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "Segoe UI", "Helvetica Neue", "Arial", "sans-serif"],
            },
            H1 = new H1Typography
            {
                FontSize = "2.5rem",
                FontWeight = "700",
            },
            H2 = new H2Typography
            {
                FontSize = "2rem",
                FontWeight = "700",
            },
            H3 = new H3Typography
            {
                FontSize = "1.75rem",
                FontWeight = "600",
            },
            H4 = new H4Typography
            {
                FontSize = "1.5rem",
                FontWeight = "600",
            },
            H5 = new H5Typography
            {
                FontSize = "1.25rem",
                FontWeight = "600",
            },
            H6 = new H6Typography
            {
                FontSize = "1.0625rem",
                FontWeight = "600",
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontWeight = "500",
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontWeight = "500",
            },
            Body1 = new Body1Typography
            {
                LineHeight = "1.6",
            },
            Body2 = new Body2Typography
            {
                LineHeight = "1.5",
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
        },
    };
}
