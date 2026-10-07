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

    /// <summary>
    /// X-02 (local test run 7): a MudTable/MudDataGrid row paints its striped or hover overlay over the
    /// table's own background, and a link inside the row sits on that composite. The dark palette set
    /// no TableStriped/TableHover, so MudBlazor's default white overlay turned an odd striped row on
    /// Surface into rgb(82,89,97), where the dark link colour #42A5F5 is only about 2.66:1. Each row
    /// overlay is alpha-blended over Surface (where tables sit) and Background (a grid with no paper)
    /// the way a browser composites it, and the link must keep the 4.5:1 text floor on the result.
    /// </summary>
    public static TheoryData<string, string> RowOverlays => new()
    {
        { nameof(Palette.TableStriped), nameof(Palette.Surface) },
        { nameof(Palette.TableHover), nameof(Palette.Surface) },
        { nameof(Palette.TableStriped), nameof(Palette.Background) },
        { nameof(Palette.TableHover), nameof(Palette.Background) },
    };

    [Theory]
    [MemberData(nameof(RowOverlays))]
    public void LinkColour_InDarkMode_MeetsTheAaTextContrastOnTableRowOverlays(string overlay, string underlay)
    {
        var palette = MMCATheme.Instance.PaletteDark;
        var link = ResolveLinkColour(palette);
        var row = Composite(PaletteColour(palette, overlay), PaletteColour(palette, underlay));

        ContrastRatio(link, row).Should().BeGreaterThanOrEqualTo(
            AaTextContrast,
            $"X-02: dark link text {link} must be legible on a row painted {overlay} over {underlay} (composited {row})");
    }

    [Theory]
    [MemberData(nameof(RowOverlays))]
    public void LinkColour_InLightMode_MeetsTheAaTextContrastOnTableRowOverlays(string overlay, string underlay)
    {
        var palette = MMCATheme.Instance.PaletteLight;
        var link = ResolveLinkColour(palette);
        var row = Composite(PaletteColour(palette, overlay), PaletteColour(palette, underlay));

        ContrastRatio(link, row).Should().BeGreaterThanOrEqualTo(
            AaTextContrast,
            $"X-02: light link text {link} must be legible on a row painted {overlay} over {underlay} (composited {row})");
    }

    private static MudColor PaletteColour(Palette palette, string name) => name switch
    {
        nameof(Palette.TableStriped) => palette.TableStriped,
        nameof(Palette.TableHover) => palette.TableHover,
        nameof(Palette.Surface) => palette.Surface,
        nameof(Palette.Background) => palette.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a palette colour this test composites"),
    };

    // Source-over alpha compositing of a (possibly translucent) overlay onto an opaque underlay, per
    // channel in sRGB, which is what a browser does for an rgba() background over a solid one.
    private static string Composite(MudColor overlay, MudColor underlay)
    {
        var alpha = overlay.A / 255.0;

        int Channel(byte top, byte bottom) =>
            (int)Math.Round(alpha * top + (1 - alpha) * bottom, MidpointRounding.AwayFromZero);

        return $"#{Channel(overlay.R, underlay.R):X2}{Channel(overlay.G, underlay.G):X2}{Channel(overlay.B, underlay.B):X2}";
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
