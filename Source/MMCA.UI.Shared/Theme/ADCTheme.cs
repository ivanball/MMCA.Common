using MudBlazor;

namespace MMCA.UI.Shared.Theme;

/// <summary>
/// Application-wide MudBlazor theme defining the brand palette, typography, and layout properties.
/// Applied via <c>MudThemeProvider</c> in the root layout.
/// </summary>
public static class ADCTheme
{
    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1565C0",
            PrimaryDarken = "#0D47A1",
            PrimaryLighten = "#42A5F5",
            Secondary = "#00897B",
            SecondaryDarken = "#00695C",
            SecondaryLighten = "#4DB6AC",
            Tertiary = "#7B1FA2",
            Info = "#1976D2",
            Success = "#2E7D32",
            Warning = "#F57F17",
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
