using AwesomeAssertions;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.Shared.Tests.Globalization;

/// <summary>
/// Tests for the culture allowlist (ADR-027), focused on <see cref="SupportedCultures.ResolveClosest"/>:
/// the fallback chain a head must apply when it resolves its own culture instead of getting request
/// localization's <c>Accept-Language</c> matching for free (MAUI Blazor Hybrid).
/// </summary>
public sealed class SupportedCulturesTests
{
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("es", "es")]
    [InlineData("ES", "es")]
    [InlineData("en-us", "en-US")]
    public void ResolveClosest_ReturnsTheAllowlistEntry_ForAnExactMatch(string input, string expected) =>
        SupportedCultures.ResolveClosest(input).Should().Be(expected);

    // The case a device locale actually hits: Android reports a specific culture, the allowlist holds a
    // neutral one. Without the language match a Spanish phone would silently start in English.
    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("es-419", "es")]
    [InlineData("en-GB", "en-US")]
    public void ResolveClosest_FallsBackToTheLanguage_ForAnUnlistedSpecificCulture(string input, string expected) =>
        SupportedCultures.ResolveClosest(input).Should().Be(expected);

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveClosest_FallsBackToTheDefault_ForAnUnsupportedLanguage(string? input) =>
        SupportedCultures.ResolveClosest(input).Should().Be(SupportedCultures.Default);

    // The pseudo locale is a Development-only diagnostic reached through the switcher and the culture
    // endpoint, never through allowlist resolution: a persisted or device value must not activate it.
    [Fact]
    public void ResolveClosest_NeverReturnsThePseudoLocale() =>
        SupportedCultures.ResolveClosest(SupportedCultures.PseudoLocale)
            .Should().Be(SupportedCultures.Default);
}
