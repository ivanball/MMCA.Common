using AwesomeAssertions;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

public class ListPageQueryStateServiceTests
{
    [Fact]
    public void ParseQueryString_Empty_ReturnsDefaults()
    {
        var state = ListPageQueryStateService.ParseQueryString(string.Empty);

        state.Page.Should().Be(0);
        state.PageSize.Should().Be(0);
        state.MobilePage.Should().Be(1);
        state.SortColumn.Should().BeNull();
        state.SortDescending.Should().BeFalse();
        state.Filters.Should().BeEmpty();
    }

    [Fact]
    public void ParseQueryString_Null_ReturnsDefaults()
    {
        var state = ListPageQueryStateService.ParseQueryString(null);

        state.Page.Should().Be(0);
        state.PageSize.Should().Be(0);
        state.MobilePage.Should().Be(1);
    }

    [Fact]
    public void ParseQueryString_AllReservedKeys_RoundTripsValues()
    {
        var state = ListPageQueryStateService.ParseQueryString("?p=2&ps=25&mp=3&s=Name&sd=desc&q=shirt&f:status=Accepted&f:category=42");

        state.Page.Should().Be(2);
        state.PageSize.Should().Be(25);
        state.MobilePage.Should().Be(3);
        state.SortColumn.Should().Be("Name");
        state.SortDescending.Should().BeTrue();
        state.Filters.Should().ContainKey("search").WhoseValue.Should().Be("shirt");
        state.Filters.Should().ContainKey("status").WhoseValue.Should().Be("Accepted");
        state.Filters.Should().ContainKey("category").WhoseValue.Should().Be("42");
    }

    [Fact]
    public void ParseQueryString_AscendingSort_DefaultsToFalse()
    {
        var state = ListPageQueryStateService.ParseQueryString("?s=Name");

        state.SortColumn.Should().Be("Name");
        state.SortDescending.Should().BeFalse();
    }

    [Fact]
    public void ParseQueryString_NoLeadingQuestionMark_StillParses()
    {
        var state = ListPageQueryStateService.ParseQueryString("p=5&q=blue");

        state.Page.Should().Be(5);
        state.Filters.Should().ContainKey("search").WhoseValue.Should().Be("blue");
    }

    [Fact]
    public void ParseQueryString_InvalidIntegers_FallBackToDefaults()
    {
        var state = ListPageQueryStateService.ParseQueryString("?p=abc&ps=NaN&mp=oops");

        state.Page.Should().Be(0);
        state.PageSize.Should().Be(0);
        state.MobilePage.Should().Be(1);
    }

    [Fact]
    public void ParseQueryString_EmptyValues_AreIgnoredFromFilters()
    {
        var state = ListPageQueryStateService.ParseQueryString("?q=&f:status=");

        state.Filters.Should().BeEmpty();
    }

    [Fact]
    public void ParseQueryString_FilterPrefixWithoutName_IsIgnored()
    {
        var state = ListPageQueryStateService.ParseQueryString("?f:=value&f:realKey=ok");

        state.Filters.Should().ContainSingle();
        state.Filters.Should().ContainKey("realKey").WhoseValue.Should().Be("ok");
    }

    [Fact]
    public void BuildPath_DefaultState_ReturnsBaseOnly()
    {
        var path = ListPageQueryStateService.BuildPath("/products", new ListPageState());

        path.Should().Be("/products");
    }

    [Fact]
    public void BuildPath_OmitsDefaultValues()
    {
        var state = new ListPageState
        {
            Page = 0,        // default — omit
            PageSize = 0,    // default — omit
            MobilePage = 1,  // default — omit
            SortColumn = null,
        };

        var path = ListPageQueryStateService.BuildPath("/orders", state);

        path.Should().Be("/orders");
    }

    [Fact]
    public void BuildPath_AllFieldsPopulated_EncodesEverything()
    {
        var state = new ListPageState
        {
            Page = 2,
            PageSize = 25,
            MobilePage = 3,
            SortColumn = "Name",
            SortDescending = true,
            Filters = new Dictionary<string, string>
            {
                ["search"] = "shirt",
                ["status"] = "Accepted",
            },
        };

        var path = ListPageQueryStateService.BuildPath("/products", state);

        path.Should().StartWith("/products?");
        path.Should().Contain("p=2");
        path.Should().Contain("ps=25");
        path.Should().Contain("mp=3");
        path.Should().Contain("s=Name");
        path.Should().Contain("sd=desc");
        path.Should().Contain("q=shirt");
        path.Should().Contain("f%3Astatus=Accepted");
    }

    [Fact]
    public void BuildPath_AscendingSort_OmitsDirectionKey()
    {
        var state = new ListPageState
        {
            SortColumn = "Name",
            SortDescending = false,
        };

        var path = ListPageQueryStateService.BuildPath("/orders", state);

        path.Should().Contain("s=Name");
        path.Should().NotContain("sd=");
    }

    [Fact]
    public void BuildPath_EmptyFilterValue_IsOmitted()
    {
        var state = new ListPageState
        {
            Filters = new Dictionary<string, string>
            {
                ["search"] = string.Empty,
                ["status"] = "Accepted",
            },
        };

        var path = ListPageQueryStateService.BuildPath("/products", state);

        path.Should().NotContain("q=");
        path.Should().Contain("f%3Astatus=Accepted");
    }

    [Fact]
    public void RoundTrip_ParseThenBuild_PreservesState()
    {
        var original = new ListPageState
        {
            Page = 4,
            PageSize = 50,
            MobilePage = 2,
            SortColumn = "CreatedOn",
            SortDescending = true,
            Filters = new Dictionary<string, string>
            {
                ["search"] = "alpha",
                ["status"] = "Pending",
                ["category"] = "7",
            },
        };

        var path = ListPageQueryStateService.BuildPath("/orders", original);
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        var query = queryStart >= 0 ? path[queryStart..] : string.Empty;
        var roundTripped = ListPageQueryStateService.ParseQueryString(query);

        roundTripped.Page.Should().Be(original.Page);
        roundTripped.PageSize.Should().Be(original.PageSize);
        roundTripped.MobilePage.Should().Be(original.MobilePage);
        roundTripped.SortColumn.Should().Be(original.SortColumn);
        roundTripped.SortDescending.Should().Be(original.SortDescending);
        roundTripped.Filters.Should().BeEquivalentTo(original.Filters);
    }
}
