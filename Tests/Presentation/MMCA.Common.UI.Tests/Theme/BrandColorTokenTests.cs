using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using MMCA.Common.UI.Theme;
using MudBlazor.Utilities;
using Xunit;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// Guards the single-source-of-truth invariant for brand colors (rubric §20): the CSS custom
/// properties in <c>wwwroot/app.css</c> must match <see cref="BrandColors"/> so the C# theme and
/// the stylesheet can't silently drift. app.css is embedded into this test assembly (see the csproj).
/// </summary>
public sealed class BrandColorTokenTests
{
    // MudColor stringifies with alpha/casing variance; compare on RGB channels for stability.
    private static string ToHex(MudColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string ReadAppCss()
    {
        using var stream = typeof(BrandColorTokenTests).Assembly.GetManifestResourceStream("app.css")
            ?? throw new InvalidOperationException("app.css must be embedded as a resource for the drift guard to run");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadCssVariable(string css, string variableName)
    {
        var match = Regex.Match(css, $@"{Regex.Escape(variableName)}\s*:\s*(#[0-9A-Fa-f]{{6}})", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        match.Success.Should().BeTrue($"app.css must define {variableName}");
        return match.Groups[1].Value;
    }

    [Theory]
    [InlineData("--mmca-primary", nameof(BrandColors.Primary))]
    [InlineData("--mmca-primary-dark", nameof(BrandColors.PrimaryDark))]
    [InlineData("--mmca-secondary", nameof(BrandColors.Secondary))]
    [InlineData("--mmca-secondary-dark", nameof(BrandColors.SecondaryDark))]
    public void CssBrandToken_MatchesBrandColorsConstant(string cssVariable, string brandConstantName)
    {
        var cssValue = ReadCssVariable(ReadAppCss(), cssVariable);

        var expected = (string)typeof(BrandColors)
            .GetField(brandConstantName, BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        cssValue.Should().BeEquivalentTo(expected,
            $"{cssVariable} in app.css must mirror BrandColors.{brandConstantName} (single source of truth)");
    }

    [Fact]
    public void ThemeLightPrimary_IsSourcedFromBrandColors()
    {
        ToHex(MMCATheme.Instance.PaletteLight.Primary).Should().BeEquivalentTo(BrandColors.Primary);
        ToHex(MMCATheme.Instance.PaletteLight.PrimaryDarken).Should().BeEquivalentTo(BrandColors.PrimaryDark);
    }

    [Fact]
    public void ThemeSecondary_IsSourcedFromBrandColors()
    {
        // The Secondary family previously hard-coded its hex in the palette (the drift the guard
        // missed, rubric §20); both palettes must now source it from BrandColors.
        ToHex(MMCATheme.Instance.PaletteLight.Secondary).Should().BeEquivalentTo(BrandColors.Secondary);
        ToHex(MMCATheme.Instance.PaletteLight.SecondaryDarken).Should().BeEquivalentTo(BrandColors.SecondaryDark);
        ToHex(MMCATheme.Instance.PaletteLight.SecondaryLighten).Should().BeEquivalentTo(BrandColors.SecondaryLight);
        ToHex(MMCATheme.Instance.PaletteDark.Secondary).Should().BeEquivalentTo(BrandColors.SecondaryLight);
    }

    [Fact]
    public void Theme_DefinesCustomDarkPalette()
    {
        // Dark mode must be supported (rubric §20 — the theme was light-only). Assert a value
        // unique to our dark palette so a missing PaletteDark (MudBlazor's default) can't pass.
        ToHex(MMCATheme.Instance.PaletteDark.Background).Should().BeEquivalentTo("#1A2027");
        ToHex(MMCATheme.Instance.PaletteDark.Primary).Should().BeEquivalentTo(BrandColors.PrimaryLight);
    }
}
