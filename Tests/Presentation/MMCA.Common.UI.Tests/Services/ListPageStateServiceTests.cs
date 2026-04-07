using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

public class ListPageStateServiceTests
{
    [Fact]
    public void SaveState_ThenGetState_RoundTripsAllFields()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        var state = new ListPageState
        {
            Page = 3,
            PageSize = 25,
            MobilePage = 2,
            ScrollPosition = 1234.5,
            Filters = new Dictionary<string, string>
            {
                ["search"] = "shirt",
                ["status"] = "Accepted",
            },
        };

        sut.SaveState("/products", state);
        var retrieved = sut.GetState("/products");

        retrieved.Should().NotBeNull();
        retrieved!.Page.Should().Be(3);
        retrieved.PageSize.Should().Be(25);
        retrieved.MobilePage.Should().Be(2);
        retrieved.ScrollPosition.Should().Be(1234.5);
        retrieved.Filters.Should().ContainKey("search").WhoseValue.Should().Be("shirt");
        retrieved.Filters.Should().ContainKey("status").WhoseValue.Should().Be("Accepted");
    }

    [Fact]
    public void GetState_ForUnknownRoute_ReturnsNull()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());

        var result = sut.GetState("/unknown");

        result.Should().BeNull();
    }

    [Fact]
    public void UpdateScrollPosition_OnExistingEntry_PreservesOtherFields()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        sut.SaveState("/orders", new ListPageState
        {
            Page = 5,
            PageSize = 50,
            MobilePage = 3,
            ScrollPosition = 100,
            Filters = new Dictionary<string, string> { ["search"] = "abc" },
        });

        sut.UpdateScrollPosition("/orders", 999.5);
        var retrieved = sut.GetState("/orders");

        retrieved.Should().NotBeNull();
        retrieved!.Page.Should().Be(5);
        retrieved.PageSize.Should().Be(50);
        retrieved.MobilePage.Should().Be(3);
        retrieved.ScrollPosition.Should().Be(999.5);
        retrieved.Filters.Should().ContainKey("search").WhoseValue.Should().Be("abc");
    }

    [Fact]
    public void UpdateScrollPosition_OnMissingEntry_CreatesMinimalEntry()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());

        sut.UpdateScrollPosition("/events", 42.0);
        var retrieved = sut.GetState("/events");

        retrieved.Should().NotBeNull();
        retrieved!.ScrollPosition.Should().Be(42.0);
        retrieved.Page.Should().Be(0);
        retrieved.PageSize.Should().Be(0);
        retrieved.MobilePage.Should().Be(1);
        retrieved.Filters.Should().BeEmpty();
    }

    [Fact]
    public void SaveState_DifferentRoutes_AreIsolated()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        sut.SaveState("/products", new ListPageState { Page = 1, PageSize = 10 });
        sut.SaveState("/categories", new ListPageState { Page = 4, PageSize = 50 });

        var products = sut.GetState("/products");
        var categories = sut.GetState("/categories");

        products!.Page.Should().Be(1);
        products.PageSize.Should().Be(10);
        categories!.Page.Should().Be(4);
        categories.PageSize.Should().Be(50);
    }

    [Fact]
    public void SaveState_OverwritesPreviousEntryForSameRoute()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        sut.SaveState("/products", new ListPageState { Page = 1, PageSize = 10 });
        sut.SaveState("/products", new ListPageState { Page = 7, PageSize = 100 });

        var retrieved = sut.GetState("/products");

        retrieved!.Page.Should().Be(7);
        retrieved.PageSize.Should().Be(100);
    }
}
