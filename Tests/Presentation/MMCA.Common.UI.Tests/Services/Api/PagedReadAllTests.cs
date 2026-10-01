using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Api;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// <see cref="PagedReadAll"/>, moved from ADC <c>Conference.UI/Services/Common/PagedReadAll.cs</c>. A
/// single read truncates silently: the API clamps any requested page size to its maximum (and the
/// GET-all endpoint ignores <c>pageSize</c>), so rows past it vanish. Paging until the reported
/// <c>TotalItemCount</c> is the only unbounded route. Ported from ADC
/// <c>SpeakerLookupServiceTests.GetAllAsync_PagesUntilTotalItems</c> (501 rows over two pages).
/// </summary>
public sealed class PagedReadAllTests
{
    [Fact]
    public void PageSize_IsTheApiDefaultMaximum() =>
        PagedReadAll.PageSize.Should().Be(500, "a smaller page wastes round trips and a larger one is clamped");

    [Fact]
    public void LookupPageUrl_BuildsTheStableIdOrderedPageUrl() =>
        PagedReadAll.LookupPageUrl("speakers", 3).Should().Be(
            "speakers/paged?pageNumber=3&pageSize=500&sortColumn=Id&sortDirection=asc&includeFKs=false&includeChildren=false");

    [Fact]
    public async Task ReadAllAsync_PagesUntilTheReportedTotal()
    {
        var rows = Enumerable.Range(1, 501).ToList();
        var requested = new List<int>();

        Result<List<int>> result = await PagedReadAll.ReadAllAsync(
            pageNumber =>
            {
                requested.Add(pageNumber);
                var items = pageNumber == 1 ? rows.Take(500) : rows.Skip(500);
                return Task.FromResult(Result.Success(Page(items, total: 501, pageNumber)));
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(rows);
        requested.Should().Equal(1, 2);
    }

    [Fact]
    public async Task ReadAllAsync_StopsOnAnEmptyPageEvenWhenTheTotalIsNotReached()
    {
        var requested = new List<int>();

        Result<List<int>> result = await PagedReadAll.ReadAllAsync(
            pageNumber =>
            {
                requested.Add(pageNumber);
                int[] items = pageNumber == 1 ? [1, 2] : [];
                return Task.FromResult(Result.Success(Page(items, total: 10, pageNumber)));
            },
            TestContext.Current.CancellationToken);

        result.Value.Should().Equal(1, 2);
        requested.Should().Equal(1, 2);
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsTheFirstFailureUnchanged()
    {
        var error = Error.Unexpected("Http.TransportFailure", "boom");
        var requested = new List<int>();

        Result<List<int>> result = await PagedReadAll.ReadAllAsync(
            pageNumber =>
            {
                requested.Add(pageNumber);
                return Task.FromResult(pageNumber == 1
                    ? Result.Success(Page([1], total: 3, pageNumber))
                    : Result.Failure<PagedCollectionResult<int>>(error));
            },
            TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Be(error);
        requested.Should().Equal(1, 2);
    }

    [Fact]
    public async Task ReadAllAsync_StopsAfterFortyPagesWhenTheServerKeepsReportingMore()
    {
        var requested = 0;

        Result<List<int>> result = await PagedReadAll.ReadAllAsync(
            pageNumber =>
            {
                requested++;
                return Task.FromResult(Result.Success(Page([pageNumber], total: int.MaxValue, pageNumber)));
            },
            TestContext.Current.CancellationToken);

        requested.Should().Be(40, "a server over-reporting its total must not spin the loop forever");
        result.Value.Should().HaveCount(40);
    }

    [Fact]
    public async Task ReadAllAsync_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => PagedReadAll.ReadAllAsync(
            pageNumber => Task.FromResult(Result.Success(Page([1], total: 1, pageNumber))),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadAllAsync_NullFetch_Throws()
    {
        var act = () => PagedReadAll.ReadAllAsync<int>(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static PagedCollectionResult<int> Page(IEnumerable<int> items, int total, int currentPage) =>
        new([.. items], new PaginationMetadata(totalItemCount: total, pageSize: 500, currentPage: currentPage));
}
