using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components.Ratings;

namespace MMCA.Common.UI.Tests.Components.Ratings;

/// <summary>
/// bUnit tests for <see cref="RatingStars"/>: one <c>role="img"</c> element carrying the caller's
/// accessible name (never five radio inputs, which axe rejects when read-only), five decorative
/// icons, and the <c>data-testid</c> / <c>data-star</c> hooks consumer E2E tests select on.
/// </summary>
public sealed class RatingStarsTests : BunitTestBase
{
    [Fact]
    public void Renders_OneImageWithTheAccessibleName()
    {
        var cut = RenderUnderTest<RatingStars>(p => p
            .Add(c => c.Value, 4)
            .Add(c => c.AriaLabel, "Rated 4 out of 5"));

        var root = cut.Find("[data-testid=rating-stars]");
        root.GetAttribute("role").Should().Be("img");
        root.GetAttribute("aria-label").Should().Be("Rated 4 out of 5");
        cut.FindAll("input").Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.0, "empty,empty,empty,empty,empty")]
    [InlineData(3.0, "full,full,full,empty,empty")]
    [InlineData(3.5, "full,full,full,half,empty")]
    [InlineData(3.49, "full,full,full,empty,empty")]
    [InlineData(4.75, "full,full,full,full,half")]
    [InlineData(5.0, "full,full,full,full,full")]
    public void Renders_FullHalfAndEmptyStarsForTheValue(double value, string expected)
    {
        var cut = RenderUnderTest<RatingStars>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.AriaLabel, "rating"));

        var stars = cut.FindAll("[data-star]").Select(e => e.GetAttribute("data-star"));

        string.Join(',', stars).Should().Be(expected);
    }

    [Fact]
    public void Renders_TheIconsAsDecorative()
    {
        var cut = RenderUnderTest<RatingStars>(p => p
            .Add(c => c.Value, 2)
            .Add(c => c.AriaLabel, "rating"));

        cut.FindAll("[data-star]").Should().HaveCount(RatingStars.MaxValue)
            .And.OnlyContain(e => e.GetAttribute("aria-hidden") == "true");
    }
}
