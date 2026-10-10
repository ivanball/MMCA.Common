using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.UI.Theme;
using MudBlazor;
using MudBlazor.Utilities;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// The label of a filled primary control against every fill MudBlazor paints it with (X-02, local
/// test run 9, WCAG 1.4.3). A filled primary button rests on <see cref="Palette.Primary"/>, but on
/// :hover, :focus-visible and :active MudBlazor repaints it with <see cref="Palette.PrimaryDarken"/>
/// while the label keeps <see cref="Palette.PrimaryContrastText"/>. The dark palette pairs the dark
/// Material on-colour rgba(0,0,0,0.87) with PrimaryDarken = BrandColors.Primary (#1565C0), so the
/// label of a focused or hovered button drops to about 3.4:1, under the 4.5:1 AA floor for text.
/// The fill is composited over Background and Surface (where buttons sit) and the translucent label
/// over that fill, the way a browser paints them, before the contrast is computed.
/// </summary>
public sealed class PrimaryFillContrastTests
{
    private const double AaTextContrast = 4.5;

    public static TheoryData<string, string> DarkFills => new()
    {
        { nameof(Palette.Primary), nameof(Palette.Background) },
        { nameof(Palette.Primary), nameof(Palette.Surface) },
        { nameof(Palette.PrimaryDarken), nameof(Palette.Background) },
        { nameof(Palette.PrimaryDarken), nameof(Palette.Surface) },
    };

    [Theory]
    [MemberData(nameof(DarkFills))]
    public void PrimaryContrastText_InDarkMode_MeetsTheAaTextContrastOnEveryPrimaryFill(string fill, string underlay)
    {
        var palette = MMCATheme.Instance.PaletteDark;
        var paintedFill = Composite(PaletteColour(palette, fill), ToHex(PaletteColour(palette, underlay)));
        var label = Composite(palette.PrimaryContrastText, paintedFill);

        ContrastRatio(label, paintedFill).Should().BeGreaterThanOrEqualTo(
            AaTextContrast,
            $"X-02 (WCAG 1.4.3): in the DARK palette the filled primary label PrimaryContrastText {ToHex(palette.PrimaryContrastText)} at alpha {palette.PrimaryContrastText.A} "
            + $"(painted {label}) must reach 4.5:1 on {fill} over {underlay} (painted {paintedFill}), because MudBlazor paints "
            + "PrimaryDarken on :hover, :focus-visible and :active of every filled primary control. Choose a dark-palette "
            + "PrimaryDarken that keeps the dark label at 4.5:1 (as Primary already does), or a label that clears 4.5:1 on both fills");
    }

    private static MudColor PaletteColour(Palette palette, string name) => name switch
    {
        nameof(Palette.Primary) => palette.Primary,
        nameof(Palette.PrimaryDarken) => palette.PrimaryDarken,
        nameof(Palette.Surface) => palette.Surface,
        nameof(Palette.Background) => palette.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a palette colour this test composites"),
    };

    // Source-over alpha compositing of a (possibly translucent) colour onto an opaque #RRGGBB underlay,
    // per channel in sRGB, which is what a browser does for an rgba() colour over a solid one.
    private static string Composite(MudColor overlay, string underlay)
    {
        var alpha = overlay.A / 255.0;

        int Channel(byte top, int offset)
        {
            var bottom = int.Parse(underlay.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (int)Math.Round(alpha * top + (1 - alpha) * bottom, MidpointRounding.AwayFromZero);
        }

        return $"#{Channel(overlay.R, 1):X2}{Channel(overlay.G, 3):X2}{Channel(overlay.B, 5):X2}";
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
}
