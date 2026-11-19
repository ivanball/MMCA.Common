using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components.Ratings;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Ratings;

/// <summary>
/// bUnit tests for <see cref="RatingInput"/>: the stars sit in a <c>role="radiogroup"</c> named by
/// the visible caption, the error is linked through <c>aria-describedby</c> only while it is shown,
/// and a selection flows out through <c>ValueChanged</c>.
/// </summary>
public sealed class RatingInputTests : BunitTestBase
{
    [Fact]
    public void Group_IsNamedByTheVisibleCaption()
    {
        var cut = RenderUnderTest<RatingInput>(p => p.Add(c => c.Label, "How was the session?"));

        var group = cut.Find("[data-testid=rating-input]");
        group.GetAttribute("role").Should().Be("radiogroup");
        var labelId = group.GetAttribute("aria-labelledby");
        labelId.Should().NotBeNullOrEmpty();
        cut.Find($"#{labelId}").TextContent.Should().Be("How was the session?");
        group.HasAttribute("aria-describedby").Should().BeFalse();
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public void Error_IsShownAndLinkedToTheGroup()
    {
        var cut = RenderUnderTest<RatingInput>(p => p
            .Add(c => c.Label, "Rating")
            .Add(c => c.ErrorText, "Pick a rating."));

        var group = cut.Find("[data-testid=rating-input]");
        var errorId = group.GetAttribute("aria-describedby");
        errorId.Should().NotBeNullOrEmpty();
        var error = cut.Find($"#{errorId}");
        error.TextContent.Should().Be("Pick a rating.");
        error.GetAttribute("role").Should().Be("alert");
    }

    [Fact]
    public void Ids_AreUniquePerInstance()
    {
        var first = RenderUnderTest<RatingInput>(p => p.Add(c => c.Label, "One"));
        var second = RenderUnderTest<RatingInput>(p => p.Add(c => c.Label, "Two"));

        first.Find("[data-testid=rating-input]").GetAttribute("aria-labelledby")
            .Should().NotBe(second.Find("[data-testid=rating-input]").GetAttribute("aria-labelledby"));
    }

    [Fact]
    public async Task Selecting_AStar_RaisesValueChanged()
    {
        var selected = 0;
        var cut = RenderUnderTest<RatingInput>(p => p
            .Add(c => c.Label, "Rating")
            .Add(c => c.ValueChanged, value => selected = value));

        var rating = cut.FindComponent<MudRating>();
        await cut.InvokeAsync(() => rating.Instance.SelectedValueChanged.InvokeAsync(4));

        selected.Should().Be(4);
    }

    [Fact]
    public void MaxValue_And_Disabled_ReachTheRating()
    {
        var cut = RenderUnderTest<RatingInput>(p => p
            .Add(c => c.Label, "Rating")
            .Add(c => c.MaxValue, 3)
            .Add(c => c.Disabled, true));

        var rating = cut.FindComponent<MudRating>().Instance;
        rating.MaxValue.Should().Be(3);
        rating.Disabled.Should().BeTrue();
    }
}
