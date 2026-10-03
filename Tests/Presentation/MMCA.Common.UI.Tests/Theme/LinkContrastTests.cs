using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using MMCA.Common.UI.Theme;
using MudBlazor;
using MudBlazor.Utilities;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// The global link colour in <c>wwwroot/app.css</c> against each palette (X-02): links, breadcrumbs
/// most visibly, rendered the raw <c>--mmca-primary</c> (#1565C0) on the dark Background #1A2027 at
/// about 2.9:1, under the WCAG 2.1 AA 4.5:1 floor for text. The rule is resolved the way a browser
/// would for each palette (a <c>--mud-palette-primary</c> reference takes that palette's Primary, a
/// <c>--mmca-primary</c> reference takes the token, which does not change with the theme), then its
/// contrast is computed against the palette's Background and Surface.
/// </summary>
public sealed partial class LinkContrastTests
{
    private const double AaTextContrast = 4.5;

    [Fact]
    public void LinkColour_InDarkMode_MeetsTheAaTextContrastOnBackgroundAndSurface()
    {
        var palette = MMCATheme.Instance.PaletteDark;
        var link = ResolveLinkColour(palette);

        ContrastRatio(link, ToHex(palette.Background)).Should().BeGreaterThanOrEqualTo(
            AaTextContrast, $"link text {link} must be legible on the dark Background");
        ContrastRatio(link, ToHex(palette.Surface)).Should().BeGreaterThanOrEqualTo(
            AaTextContrast, $"link text {link} must be legible on the dark Surface");
    }

    [Fact]
    public void LinkColour_InLightMode_IsTheUnchangedBrandPrimary()
    {
        var palette = MMCATheme.Instance.PaletteLight;

        ResolveLinkColour(palette).Should().BeEquivalentTo(BrandColors.Primary);
        ContrastRatio(BrandColors.Primary, ToHex(palette.Surface)).Should().BeGreaterThanOrEqualTo(AaTextContrast);
    }

    private static string ResolveLinkColour(Palette palette)
    {
        var css = ReadAppCss();
        var rule = LinkRule.Match(css);
        rule.Success.Should().BeTrue("app.css must declare the global link colour");

        // The first custom property referenced is the one a page with a MudThemeProvider resolves.
        var reference = CustomPropertyReference.Match(rule.Groups["value"].Value);
        reference.Success.Should().BeTrue("the link colour must come from a custom property, not a literal");

        return reference.Groups["name"].Value switch
        {
            "--mud-palette-primary" => ToHex(palette.Primary),
            var token => Regex.Match(css, $@"{Regex.Escape(token)}\s*:\s*(#[0-9A-Fa-f]{{6}})", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value,
        };
    }

    private static string ReadAppCss()
    {
        using var stream = typeof(LinkContrastTests).Assembly.GetManifestResourceStream("app.css")
            ?? throw new InvalidOperationException("app.css must be embedded as a resource");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ToHex(MudColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static double ContrastRatio(string foreground, string background)
    {
        var lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        var darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    // WCAG 2.1 relative luminance of an sRGB #RRGGBB colour.
    private static double RelativeLuminance(string hex)
    {
        static double Channel(string hex, int offset)
        {
            var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);
    }

    [GeneratedRegex(@"(?m)^a\s*\{\s*color:\s*(?<value>[^;]+);", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinkRule { get; }

    [GeneratedRegex(@"var\(\s*(?<name>--[a-z0-9-]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CustomPropertyReference { get; }
}
