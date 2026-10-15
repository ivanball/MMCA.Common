using AwesomeAssertions;
using MMCA.Common.UI.Theme;
using MudBlazor;
using MudBlazor.Utilities;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// The message text of a text (default-variant) alert in the DARK palette (X-02, local test run 12,
/// WCAG 1.4.3). MudBlazor 9.11 colours <c>.mud-alert-text-{severity}</c> with
/// <c>--mud-palette-{severity}-darken</c> over a background of <c>--mud-palette-{severity}-hover</c>,
/// which is the severity's base colour at <see cref="Palette.HoverOpacity"/> composited on the
/// alert's Surface. Left unset, the *Darken colours are auto-darkened, which on a dark surface moves
/// the text towards the background (Info #2094F3 is 3.82:1 on its tint). Every ratio is computed on
/// the colours a browser actually paints, reusing the helpers of <see cref="PrimaryFillContrastTests"/>.
/// </summary>
public sealed class AlertTextContrastTests
{
    private const double AaTextContrast = 4.5;

    public static TheoryData<string> Severities => new()
    {
        nameof(Palette.Info),
        nameof(Palette.Success),
        nameof(Palette.Warning),
        nameof(Palette.Error),
    };

    [Theory]
    [MemberData(nameof(Severities))]
    public void AlertText_InDarkMode_MeetsTheAaTextContrastOnSurface(string severity)
    {
        var palette = MMCATheme.Instance.PaletteDark;
        var (_, darken) = Colours(palette, severity);
        var surface = PrimaryFillContrastTests.ToHex(palette.Surface);
        var text = PrimaryFillContrastTests.ToHex(darken);

        PrimaryFillContrastTests.ContrastRatio(text, surface).Should().BeGreaterThanOrEqualTo(
            AaTextContrast,
            $"X-02 (WCAG 1.4.3): the dark-palette {severity}Darken {text} is the text colour of a text or outlined "
            + $"{severity} alert and must reach 4.5:1 on Surface {surface}");
    }

    [Theory]
    [MemberData(nameof(Severities))]
    public void AlertText_InDarkMode_MeetsTheAaTextContrastOnTheAlertTint(string severity)
    {
        var palette = MMCATheme.Instance.PaletteDark;
        var (baseColour, darken) = Colours(palette, severity);
        var surface = PrimaryFillContrastTests.ToHex(palette.Surface);

        // --mud-palette-{severity}-hover: the base colour at HoverOpacity, painted over Surface.
        var tint = PrimaryFillContrastTests.Composite(baseColour.SetAlpha(palette.HoverOpacity), surface);
        var text = PrimaryFillContrastTests.ToHex(darken);

        PrimaryFillContrastTests.ContrastRatio(text, tint).Should().BeGreaterThanOrEqualTo(
            AaTextContrast,
            $"X-02 (WCAG 1.4.3): the dark-palette {severity}Darken {text} must reach 4.5:1 on the text alert's "
            + $"background, {severity} {PrimaryFillContrastTests.ToHex(baseColour)} at alpha {palette.HoverOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture)} over "
            + $"Surface {surface} (painted {tint}). Choose a lighter {severity}Darken in the dark palette");
    }

    private static (MudColor Base, MudColor Darken) Colours(Palette palette, string severity) => severity switch
    {
        nameof(Palette.Info) => (palette.Info, palette.InfoDarken),
        nameof(Palette.Success) => (palette.Success, palette.SuccessDarken),
        nameof(Palette.Warning) => (palette.Warning, palette.WarningDarken),
        nameof(Palette.Error) => (palette.Error, palette.ErrorDarken),
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "not an alert severity"),
    };
}
