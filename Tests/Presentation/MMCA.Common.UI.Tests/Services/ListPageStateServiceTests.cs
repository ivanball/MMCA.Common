using AwesomeAssertions;
using Bunit;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

public class ListPageStateServiceTests
{
    private const string NavInteropModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

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
            DenseGrid = true,
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
        retrieved.DenseGrid.Should().BeTrue();
        retrieved.Filters.Should().ContainKey("search").WhoseValue.Should().Be("shirt");
        retrieved.Filters.Should().ContainKey("status").WhoseValue.Should().Be("Accepted");
    }

    [Fact]
    public void DenseGrid_DefaultsToFalse()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        sut.SaveState("/products", new ListPageState { Page = 1, PageSize = 10 });

        sut.GetState("/products")!.DenseGrid.Should().BeFalse();
    }

    [Fact]
    public void UpdateScrollPosition_PreservesDenseGrid()
    {
        var sut = new ListPageStateService(Mock.Of<IJSRuntime>());
        sut.SaveState("/orders", new ListPageState { Page = 2, PageSize = 25, DenseGrid = true });

        sut.UpdateScrollPosition("/orders", 500);

        sut.GetState("/orders")!.DenseGrid.Should().BeTrue();
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

    // ==================== Store Run 3 D7: logout forgets list-page state ====================
    // The search and filters live in sessionStorage under mmca.lps:{route}, which outlives the
    // session in the same tab. Sign-out forgets them, and only them.
    [Fact]
    public async Task ClearAsync_ForgetsEveryRouteHeldInMemory()
    {
        var (sut, _) = CreateWithSessionStorage();
        sut.SaveState("/my-orders", new ListPageState { Filters = new Dictionary<string, string> { ["search"] = "previous user" } });
        sut.SaveState("/products", new ListPageState { Page = 3, PageSize = 25 });

        await sut.ClearAsync();

        sut.GetState("/my-orders").Should().BeNull();
        sut.GetState("/products").Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesOnlyTheListPageKeysFromSessionStorage()
    {
        var (sut, module) = CreateWithSessionStorage();

        await sut.ClearAsync();

        var removal = module.Invocations["sessionRemoveByPrefix"].Should().ContainSingle().Subject;
        removal.Arguments.Should().ContainSingle().Which.Should().Be(
            "mmca.lps:",
            "the removal is scoped to the list-page prefix, so the pending toast, navigation history and every other sessionStorage key survive");
        module.Invocations.Should().ContainSingle("nothing else is touched: no per-key removal, no blanket clear");
    }

    [Fact]
    public async Task ClearAsync_WhenTheCircuitIsGone_IsSwallowed()
    {
        var interop = new BunitJSInterop();
        interop.SetupModule(NavInteropModulePath)
            .SetupVoid("sessionRemoveByPrefix", _ => true)
            .SetException(new JSDisconnectedException("circuit closed"));
        var sut = new ListPageStateService(interop.JSRuntime);
        sut.SaveState("/my-orders", new ListPageState { Page = 1 });

        var act = async () => await sut.ClearAsync();

        await act.Should().NotThrowAsync("a storage failure must never strand a user inside a session they asked to leave");
        sut.GetState("/my-orders").Should().BeNull("the in-memory half clears whatever the storage did");
    }

    [Fact]
    public async Task ClearAsync_WhenTheInteropCallTimesOut_IsSwallowed()
    {
        var interop = new BunitJSInterop();
        interop.SetupModule(NavInteropModulePath)
            .SetupVoid("sessionRemoveByPrefix", _ => true)
            .SetException(new TaskCanceledException("interop timed out"));
        var sut = new ListPageStateService(interop.JSRuntime);
        sut.SaveState("/my-orders", new ListPageState { Page = 1 });

        var act = async () => await sut.ClearAsync();

        await act.Should().NotThrowAsync("sign-out awaits this before flipping the auth state to anonymous, so a timeout must not escape");
        sut.GetState("/my-orders").Should().BeNull("the in-memory half clears whatever the storage did");
    }

    // L248: hydrate is awaited from a page's first render and persist is fire-and-forget from
    // DataGridListPageBase, so an interop timeout must not escape either.
    [Fact]
    public async Task HydrateFromSessionAsync_WhenTheInteropCallTimesOut_IsSwallowed()
    {
        var interop = new BunitJSInterop();
        interop.SetupModule(NavInteropModulePath)
            .Setup<ListPageState?>("sessionGet", _ => true)
            .SetException(new TaskCanceledException("interop timed out"));
        var sut = new ListPageStateService(interop.JSRuntime);

        var act = async () => await sut.HydrateFromSessionAsync("/products");

        await act.Should().NotThrowAsync("a storage timeout must not break the page that hydrates");
        sut.GetState("/products").Should().BeNull();
    }

    [Fact]
    public async Task PersistToSessionAsync_WhenTheInteropCallTimesOut_IsSwallowed()
    {
        var interop = new BunitJSInterop();
        interop.SetupModule(NavInteropModulePath)
            .SetupVoid("sessionSet", _ => true)
            .SetException(new TaskCanceledException("interop timed out"));
        var sut = new ListPageStateService(interop.JSRuntime);
        sut.SaveState("/products", new ListPageState { Page = 2 });

        var act = async () => await sut.PersistToSessionAsync("/products");

        await act.Should().NotThrowAsync("the grid fires this and forgets it, so a timeout would be an unobserved fault");
        sut.GetState("/products")!.Page.Should().Be(2, "the in-memory state stands whatever the storage did");
    }

    private static (ListPageStateService Sut, BunitJSModuleInterop Module) CreateWithSessionStorage()
    {
        var interop = new BunitJSInterop();
        var module = interop.SetupModule(NavInteropModulePath);
        module.SetupVoid("sessionRemoveByPrefix", _ => true).SetVoidResult();
        return (new ListPageStateService(interop.JSRuntime), module);
    }
}
