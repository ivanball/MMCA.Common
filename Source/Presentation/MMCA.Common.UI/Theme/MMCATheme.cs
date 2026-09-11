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
            // tokens --mmca-primary / --mmca-primary-dark in wwwroot/app.css must mirror these.
            // BrandColorTokenTests asserts the two stay in sync.
            Primary = BrandColors.Primary,
            PrimaryDarken = BrandColors.PrimaryDark,
            PrimaryLighten = BrandColors.PrimaryLight,
            // Contrast rationale lives on BrandColors.Secondary (Teal 700, WCAG AA on light surfaces).
            Secondary = BrandColors.Secondary,
            SecondaryDarken = BrandColors.SecondaryDark,
            SecondaryLighten = BrandColors.SecondaryLight,
            Tertiary = "#7B1FA2",
            Info = "#1976D2",
            Success = "#2E7D32",
            // Amber 900 rather than the Material amber (#F57F17): the palette colour is used as TEXT
            // and as a border as often as it is used as a fill (Color.Warning on MudText/MudLink/
            // MudIcon and on outlined chips/buttons), and #F57F17 is only ~2.65:1 on Surface #FFFFFF,
            // failing the WCAG 2.1 AA 4.5:1 floor in every one of those places. #A85D00 is 4.96:1 on
            // Surface and 4.79:1 on Background #FAFBFC, and it is dark enough that white becomes the
            // correct on-colour label: 4.96:1 (dark text would be only ~3.2:1 on it, so the two values
            // must move together). Info/Success/Error already pass with white.
            Warning = "#A85D00",
            WarningContrastText = "#FFFFFF",
            Error = "#C62828",
            // Outlined-field borders are non-text UI, so they answer to the 3:1 floor (WCAG 1.4.11).
            // MudBlazor's default rgba(0,0,0,0.42) is 3.03:1 on Surface and 3.01:1 on Background: it
            // passes with no margin at all, and any host that nudges Background darker drops it below.
            // rgba(0,0,0,0.45) is 3.36:1 / 3.33:1 and keeps the same hairline weight.
            LinesInputs = "rgba(0,0,0,0.45)",
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
            // Material dark-theme treatment: a lightened primary takes DARK on-color text. The default
            // white label is ~2.65:1 on #42A5F5 and fails the WCAG 2.1 AA 4.5:1 floor on every filled
            // primary button (caught by the gated dark-mode axe scan); dark text is ~6.6:1.
            PrimaryContrastText = "rgba(0,0,0,0.87)",
            Secondary = BrandColors.SecondaryLight,
            SecondaryDarken = "#00897B",
            SecondaryLighten = "#80CBC4",
            // Every lightened dark-mode accent takes the same Material treatment as Primary above:
            // white on it is far below the 4.5:1 floor once it is used as a FILL (2.44:1 on Secondary
            // #4DB6AC, 2.39:1 on Tertiary #CE93D8, 2.65:1 on Info #42A5F5, 2.36:1 on Success #66BB6A),
            // while rgba(0,0,0,0.87) lands at 7.45 / 7.60 / 6.96 / 7.70:1. Each accent stays legible
            // as TEXT on Surface #27303A too (5.48 / 5.60 / 5.05 / 5.66:1), so only the on-colour
            // label needed fixing.
            SecondaryContrastText = "rgba(0,0,0,0.87)",
            Tertiary = "#CE93D8",
            TertiaryContrastText = "rgba(0,0,0,0.87)",
            Info = "#42A5F5",
            InfoContrastText = "rgba(0,0,0,0.87)",
            Success = "#66BB6A",
            SuccessContrastText = "rgba(0,0,0,0.87)",
            Warning = "#FFA726",
            // Same fix as the light palette: white on #FFA726 is ~2.0:1; dark text is ~10.8:1.
            WarningContrastText = "rgba(0,0,0,0.87)",
            // Red 200 rather than Red 400 (#EF5350): as TEXT on Surface #27303A the darker red is only
            // 3.84:1, below the 4.5:1 floor wherever Color.Error is a label rather than a fill (inline
            // validation copy, outlined error chips, the mobile load-failure line). #FF8A80 reads
            // 5.86:1 on Surface and 7.19:1 on Background #1A2027.
            Error = "#FF8A80",
            // Same treatment as Primary: white on a lightened dark-mode error fails AA on the filled
            // error alert's message text; rgba(0,0,0,0.87) on #FF8A80 is 7.94:1.
            ErrorContrastText = "rgba(0,0,0,0.87)",
            // Non-text UI answers to the 3:1 floor (WCAG 1.4.11). MudBlazor's dark default
            // rgba(255,255,255,0.3) is only 2.6:1 on Surface #27303A, so outlined text-field borders
            // are effectively invisible to a low-vision user; rgba(255,255,255,0.5) is 4.59:1 on
            // Surface and 5.10:1 on Background without turning the hairline into a hard outline.
            LinesInputs = "rgba(255,255,255,0.5)",
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
                // Inter is self-hosted by this RCL (wwwroot/fonts + the @font-face block in
                // wwwroot/app.css). Before those faces existed this stack silently fell through to
                // Segoe UI, so keep the two in step when the family here changes.
                FontFamily = ["Inter", "Segoe UI", "Helvetica Neue", "Arial", "sans-serif"],
            },
            // Heading scale: display weights (700-800) on h1-h4 with a slight negative tracking, which
            // is how Inter is meant to be set at large sizes (its default spacing is tuned for body
            // text and reads loose in a headline). h5/h6 stay at 600, close enough to body size that
            // negative tracking would only cost legibility.
            H1 = new H1Typography
            {
                FontSize = "2.5rem",
                FontWeight = "800",
                LetterSpacing = "-0.025em",
                LineHeight = "1.15",
            },
            H2 = new H2Typography
            {
                FontSize = "2rem",
                FontWeight = "800",
                LetterSpacing = "-0.02em",
                LineHeight = "1.2",
            },
            H3 = new H3Typography
            {
                FontSize = "1.75rem",
                FontWeight = "700",
                LetterSpacing = "-0.015em",
                LineHeight = "1.25",
            },
            H4 = new H4Typography
            {
                FontSize = "1.5rem",
                FontWeight = "700",
                LetterSpacing = "-0.01em",
                LineHeight = "1.3",
            },
            H5 = new H5Typography
            {
                FontSize = "1.25rem",
                FontWeight = "600",
                LineHeight = "1.35",
            },
            H6 = new H6Typography
            {
                FontSize = "1.0625rem",
                FontWeight = "600",
                LineHeight = "1.4",
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
            // Sentence-case buttons: MudBlazor's default UPPERCASES every label, which wrecks
            // localized strings (German compounds, accented capitals) and reads dated next to the
            // rest of the scale. Weight 600 keeps the label as prominent as the shouting did.
            Button = new ButtonTypography
            {
                FontWeight = "600",
                LetterSpacing = "0.01em",
                TextTransform = "none",
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
        },
    };
}
