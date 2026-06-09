using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components;

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
