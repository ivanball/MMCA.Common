using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Adversarial coverage for <see cref="BrandColorTokenTestsBase"/>, the brand-token drift guard Store
/// and ADC subclass over their embedded landing-page stylesheets. This assembly embeds four fixture
/// stylesheets (see the csproj): one that consumes the token, one that consumes it but also re-hardcodes
/// the hex in lower case, one that never names the token, and a blank one. The subclasses are private so
/// xUnit does not collect their inherited fact as a (deliberately failing) test of its own.
/// </summary>
public sealed class BrandColorTokenTestsBaseTests
{
    [Fact]
    public void StylesheetConsumingTheToken_Passes()
    {
        var act = new Guard("fixtures.brand-token.css").LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        act.Should().NotThrow("the stylesheet sources the brand color from var(--mmca-primary) and names no hex");
    }

    [Fact]
    public void ReHardcodedHex_IsFlagged_WhateverItsCase()
    {
        var act = new Guard("fixtures.brand-token.css", "fixtures.brand-hardcoded.css").LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain("fixtures.brand-hardcoded.css must not re-hardcode the primary brand hex #1565C0; use var(--mmca-primary)");
        message.Should().NotContain("fixtures.brand-hardcoded.css must source", "the stylesheet does consume the token");
        message.Should().NotContain("fixtures.brand-token.css", "the compliant stylesheet beside it is not reported");
        message.Should().Contain("host copies cannot drift from the framework palette");
    }

    [Fact]
    public void StylesheetWithoutTheToken_IsFlagged()
    {
        var act = new Guard("fixtures.brand-untokenized.css").LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain("fixtures.brand-untokenized.css must source the primary brand color from var(--mmca-primary) (single source of truth)");
        message.Should().NotContain("must not re-hardcode", "the stylesheet names no brand hex");
    }

    [Fact]
    public void BlankStylesheet_IsFlaggedOnlyAsEmpty()
    {
        var act = new Guard("fixtures.brand-blank.css").LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain("fixtures.brand-blank.css must be a non-empty stylesheet");
        message.Should().NotContain("must source the primary brand color", "a blank stylesheet is reported once, as blank");
    }

    [Fact]
    public void NoListedStylesheet_FailsAsVacuous()
    {
        var act = new Guard().LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("at least one landing-page stylesheet must be embedded and listed");
    }

    [Fact]
    public void ListedStylesheetThatIsNotEmbedded_FailsNamingIt()
    {
        var act = new Guard("fixtures.not-embedded.css").LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("fixtures.not-embedded.css must be embedded as a resource for the brand-token drift guard to run");
    }

    /// <summary>The guard over a chosen set of this assembly's embedded stylesheets.</summary>
    private sealed class Guard(params string[] logicalNames) : BrandColorTokenTestsBase
    {
        protected override IReadOnlyList<string> EmbeddedCssLogicalNames => logicalNames;
    }
}
