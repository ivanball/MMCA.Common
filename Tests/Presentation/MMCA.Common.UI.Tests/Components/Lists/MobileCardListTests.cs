using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.UI.Components.Lists;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Lists;

public sealed class MobileCardListTests : BunitTestBase
{
    [Fact]
    public void Renders_EmptyState_WhenNoItems()
    {
        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, Array.Empty<string>())
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.EmptyMessage, "No widgets"));

        cut.Markup.Should().Contain("No widgets");
        cut.FindComponents<MudCard>().Should().BeEmpty();
    }

    [Fact]
    public void Renders_OneCardPerItem()
    {
        var items = new List<string> { "Alpha", "Bravo", "Charlie" };

        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.TotalItems, items.Count)
            .Add(c => c.CardTemplate, item => item));

        cut.Markup.Should().Contain("Alpha").And.Contain("Bravo").And.Contain("Charlie");
        cut.FindComponents<MudCard>().Count.Should().Be(3);
    }

    [Fact]
    public void ShowsPagination_WhenTotalItemsExceedPageSize()
    {
        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, new List<string> { "first-page-item" })
            .Add(c => c.TotalItems, 25)
            .Add(c => c.PageSize, 10)
            .Add(c => c.CardTemplate, item => item));

        cut.FindComponents<MudPagination>().Count.Should().Be(1);
    }

    [Fact]
    public void LoadingBar_SitsInsideAPoliteLiveRegion()
    {
        // MudProgressLinear on its own is an anonymous progressbar nobody is told about; the
        // role="status" wrapper plus the bar's own name is the shape PageLoadingState established.
        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, Array.Empty<string>())
            .Add(c => c.IsLoading, true)
            .Add(c => c.CardTemplate, item => item));

        var region = cut.Find("[role=status]");
        region.GetAttribute("aria-live").Should().Be("polite");
        region.GetAttribute("aria-busy").Should().Be("true");
        cut.Find("[role=status] .mud-progress-linear").HasAttribute("aria-label").Should().BeTrue();
    }

    [Fact]
    public void CardsAreKeyboardOperable_WhenAClickCallbackIsWired()
    {
        // WCAG 2.1.1: the row used to be reachable by pointer only.
        string? activated = null;
        var items = new List<string> { "Alpha", "Bravo" };

        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.TotalItems, items.Count)
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.OnCardClick, EventCallback.Factory.Create<string>(this, s => activated = s)));

        var card = cut.FindAll(".mobile-list-card")[1];
        card.GetAttribute("tabindex").Should().Be("0");
        card.GetAttribute("role").Should().Be("button");
        card.KeyDown(new KeyboardEventArgs { Key = " " });

        activated.Should().Be("Bravo");
    }

    [Fact]
    public void WithoutAClickCallback_CardsStayOutOfTheTabOrder()
    {
        var items = new List<string> { "Alpha" };

        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.TotalItems, items.Count)
            .Add(c => c.CardTemplate, item => item));

        var card = cut.Find(".mobile-list-card");
        card.HasAttribute("tabindex").Should().BeFalse();
        card.HasAttribute("role").Should().BeFalse();
    }

    [Fact]
    public void HidesPagination_WhenResultsFitOnOnePage()
    {
        var items = new List<string> { "a", "b" };

        var cut = RenderUnderTest<MobileCardList<string>>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.TotalItems, items.Count)
            .Add(c => c.PageSize, 10)
            .Add(c => c.CardTemplate, item => item));

        cut.FindComponents<MudPagination>().Should().BeEmpty();
    }
}
