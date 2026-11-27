using System.Linq.Expressions;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Lists;
using MMCA.Common.UI.Pages.Common;
using Moq;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Components.Lists;

/// <summary>
/// X-17 (ADC local test run 21): a list page rendered through <see cref="ListPageShell{TItem}"/> took the
/// desktop grid on its first render, before the viewport answered, and a phone then swapped it for the
/// card list: two fetches and a layout shift that moved everything below the list. The shell now holds
/// both layouts back until <see cref="DataGridListPageBase{TDto}"/> has its first viewport notification,
/// with no markup change on the page, and the layout that notification picks fetches once.
/// </summary>
public sealed class ListPageShellLayoutGateTests : BunitTestBase
{
    private static readonly BrowserWindowSize PhoneViewport = new() { Width = 390, Height = 844 };
    private static readonly BrowserWindowSize DesktopViewport = new() { Width = 1280, Height = 800 };

    // Subscribes and never answers, so each test delivers the first notification itself.
    private readonly Mock<IBrowserViewportService> _viewport = new();

    public ListPageShellLayoutGateTests()
    {
        Services.AddSingleton(new Mock<IToastService>().Object);
        _viewport
            .Setup(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        Services.AddSingleton(_viewport.Object);
        ConfigureDataGridListPageHost(stubViewport: false);
    }

    public sealed record Row(int Id, string Name);

    /// <summary>
    /// A list page wired to the shell the way every consumer admin list is: <c>ServerData</c> bound to
    /// the page's own method, the phone fetcher from <c>MobilePageFetcher</c>, and no layout parameter.
    /// </summary>
    private sealed class ShellPage : DataGridListPageBase<Row>
    {
        private static readonly IReadOnlyList<Row> Rows = [new(1, "Alpha"), new(2, "Bravo")];

        private ListPageShell<Row>? _shell;

        public List<int> GridFetchedPages { get; } = [];

        public int MobileFetches { get; private set; }

        public bool LayoutKnown => IsLayoutKnown;

        protected override string Title => "Rows";

        protected override MudDataGrid<Row>? GridRef => _shell?.Grid;

        protected override MobileInfiniteScrollList<Row>? MobileListRef => _shell?.MobileList;

        private Func<int, int, CancellationToken, Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>>> FetchMobilePage =>
            MobilePageFetcher((_, _, _, _, _, _) =>
            {
                MobileFetches++;
                return Page();
            });

        private Task<GridData<Row>> LoadServerData(GridState<Row> state, CancellationToken cancellationToken) =>
            LoadServerDataAsync(state, (_, pageNumber, _, _, _, _) =>
            {
                GridFetchedPages.Add(pageNumber);
                return Page();
            });

        private static Task<Result<(IReadOnlyList<Row> Items, int TotalItems)>> Page() =>
            Task.FromResult(Result.Success<(IReadOnlyList<Row> Items, int TotalItems)>((Rows, 50)));

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ListPageShell<Row>>(0);
            builder.AddComponentParameter(1, nameof(ListPageShell<>.Title), Title);
            builder.AddComponentParameter(2, nameof(ListPageShell<>.IsLoading), IsLoading);
            builder.AddComponentParameter(3, nameof(ListPageShell<>.IsMobile), IsMobile);
            builder.AddComponentParameter(4, nameof(ListPageShell<>.MobileFetch), FetchMobilePage);
            builder.AddComponentParameter(5, nameof(ListPageShell<>.CardTemplate), (RenderFragment<Row>)(row => b => b.AddContent(0, row.Name)));
            builder.AddComponentParameter(6, nameof(ListPageShell<>.ServerData), (Func<GridState<Row>, CancellationToken, Task<GridData<Row>>>)LoadServerData);
            builder.AddComponentParameter(7, nameof(ListPageShell<>.CurrentPage), CurrentPageState);
            builder.AddComponentParameter(8, nameof(ListPageShell<>.RowsPerPage), RowsPerPageState);
            builder.AddComponentParameter(9, nameof(ListPageShell<>.Columns), NameColumn);
            builder.AddComponentParameter(10, nameof(ListPageShell<>.ShowDeleteConfirmation), false);
            builder.AddComponentReferenceCapture(11, shell => _shell = (ListPageShell<Row>)shell);
            builder.CloseComponent();
        }

        private static RenderFragment NameColumn => builder =>
        {
            builder.OpenComponent<PropertyColumn<Row, string>>(0);
            builder.AddAttribute(1, nameof(PropertyColumn<,>.Property), (Expression<Func<Row, string>>)(row => row.Name));
            builder.CloseComponent();
        };
    }

    [Fact]
    public async Task BeforeTheFirstViewportNotification_NeitherLayoutRenders_AndNothingFetches()
    {
        var cut = Render<ShellPage>();
        await cut.WaitForAssertionAsync(() => _viewport.Verify(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()), Times.Once));

        cut.Instance.LayoutKnown.Should().BeFalse();
        cut.FindComponents<MudDataGrid<Row>>().Should().BeEmpty("the desktop grid is only IsMobile's default here");
        cut.FindComponents<MobileInfiniteScrollList<Row>>().Should().BeEmpty();
        cut.Find(".list-layout-pending").Should().NotBeNull("the placeholder (min-height: 100vh in app.css) reserves the viewport so nothing below it moves");
        cut.Find(".list-layout-pending").GetAttribute("aria-busy").Should().Be("true");
        cut.Instance.GridFetchedPages.Should().BeEmpty();
        cut.Instance.MobileFetches.Should().Be(0);
    }

    [Fact]
    public async Task AFirstPhoneNotification_RendersOnlyTheCardList_WhichFetchesOnce()
    {
        var cut = Render<ShellPage>();
        await cut.WaitForAssertionAsync(() => _viewport.Verify(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()), Times.Once));

        await NotifyAsync(cut, PhoneViewport, Breakpoint.Xs);

        await cut.WaitForAssertionAsync(() => cut.FindComponents<MudCard>().Count.Should().Be(2), TimeSpan.FromSeconds(5));
        cut.FindComponents<MobileInfiniteScrollList<Row>>().Should().ContainSingle();
        cut.FindComponents<MudDataGrid<Row>>().Should().BeEmpty();
        cut.FindAll(".list-layout-pending").Should().BeEmpty();
        cut.Instance.MobileFetches.Should().Be(1);
        cut.Instance.GridFetchedPages.Should().BeEmpty("the grid never rendered on the phone");
    }

    [Fact]
    public async Task AFirstDesktopNotification_RendersOnlyTheGrid_WhichFetchesOnce()
    {
        var cut = Render<ShellPage>();
        await cut.WaitForAssertionAsync(() => _viewport.Verify(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()), Times.Once));

        await NotifyAsync(cut, DesktopViewport, Breakpoint.Lg);

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Alpha"), TimeSpan.FromSeconds(5));
        cut.FindComponents<MudDataGrid<Row>>().Should().ContainSingle();
        cut.FindComponents<MobileInfiniteScrollList<Row>>().Should().BeEmpty();
        cut.FindAll(".list-layout-pending").Should().BeEmpty();
        cut.Instance.GridFetchedPages.Should().Equal([1], "the grid's own first load is the only fetch");
        cut.Instance.MobileFetches.Should().Be(0);
    }

    // The grid now renders after the first-render setup, so the page restored from the URL has to reach
    // that later grid: its first load asks for it and the pager lands on it, as when the grid rendered first.
    [Fact]
    public async Task AFirstDesktopNotification_PutsTheUrlPageOnTheGridThatRendersAfterIt()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("?p=2&ps=10");

        var cut = Render<ShellPage>();
        await cut.WaitForAssertionAsync(() => _viewport.Verify(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()), Times.Once));

        await NotifyAsync(cut, DesktopViewport, Breakpoint.Lg);

        await cut.WaitForAssertionAsync(() =>
        {
            var grid = cut.FindComponent<MudDataGrid<Row>>().Instance;
            grid.CurrentPage.Should().Be(2);
            grid.RowsPerPage.Should().Be(10);
            cut.Instance.GridFetchedPages.Should().NotBeEmpty().And.OnlyContain(page => page == 3, "every load asks for the restored page");
        }, TimeSpan.FromSeconds(5));
        cut.Instance.MobileFetches.Should().Be(0);
    }

    private static Task NotifyAsync(IRenderedComponent<ShellPage> cut, BrowserWindowSize size, Breakpoint breakpoint) =>
        cut.InvokeAsync(() => cut.Instance.NotifyBrowserViewportChangeAsync(
            new BrowserViewportEventArgs(Guid.NewGuid(), size, breakpoint)));
}
