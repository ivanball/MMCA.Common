using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Auditing;
using MMCA.Common.UI.Components.PageState;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Auditing;

/// <summary>
/// Pins <see cref="AuditHistoryPanel"/> (item 12, UI side): the opt-in "who changed what, and
/// when" table over a caller-supplied page fetcher. It fetches nothing during prerender, renders
/// entries in the order received with the contract's six columns, pages by "Load more" while a
/// page comes back full, shows the shared empty and failure states, discards a superseded
/// response after <see cref="AuditHistoryPanel.ReloadAsync"/>, and cancels its fetch on dispose.
/// </summary>
public sealed class AuditHistoryPanelTests : BunitTestBase
{
    private static readonly DateTime BaseInstant = new(2026, 11, 11, 14, 30, 0, DateTimeKind.Utc);

    private readonly List<(int Page, int PageSize)> _calls = [];

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // Guard and prerender (rules 4, 5)
    [Fact]
    public void MissingFetchPage_Throws()
    {
        Interactive();

        var render = () => RenderUnderTest<AuditHistoryPanel>(_ => { });

        render.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Prerender_DoesNotFetch()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        RenderUnderTest<AuditHistoryPanel>(p => p.Add(c => c.FetchPage, Serve([Entry(1)])));

        _calls.Should().BeEmpty("nothing is fetched while the renderer is not interactive");
    }

    [Fact]
    public void FirstRender_FetchesPageOneWithPageSize()
    {
        var cut = RenderPanel(Serve([Entry(1)]), p => p.Add(c => c.PageSize, 7));

        cut.WaitForAssertion(() => _calls.Should().Equal((1, 7)));
    }

    [Fact]
    public void FirstRender_DefaultPageSizeIsTwenty()
    {
        var cut = RenderPanel(Serve([Entry(1)]));

        cut.WaitForAssertion(() => _calls.Should().Equal((1, 20)));
    }

    // Rows (rule 6)
    [Fact]
    public void RendersOneRowPerEntryInReceivedOrder()
    {
        // ChangedOn deliberately out of order: the panel renders what it received and does not sort.
        IReadOnlyList<AuditHistoryEntryDTO> page =
        [
            Entry(5, oldValue: "first"),
            Entry(1, oldValue: "second"),
            Entry(10, oldValue: "third"),
        ];

        var cut = RenderPanel(Serve(page));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(3));
        cut.FindAll("tbody tr").Select(r => Cells(r)[4]).Should().Equal("first", "second", "third");
    }

    [Fact]
    public void Row_RendersTheSixColumnsInContractOrder()
    {
        var entry = Entry(0, operation: "Modified", changedBy: 42, property: "Name", oldValue: "Alpha", newValue: "Beta");

        var cut = RenderPanel(Serve([entry]));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        var cells = Cells(cut.Find("tbody tr"));
        cells.Should().HaveCount(6);
        cells[0].Should().Contain(BaseInstant.ToString("g", CultureInfo.CurrentCulture), "when: UTC by default");
        cells[1].Should().Be("42", "who: the id's invariant string when no UserLabel is set");
        cells[2].Should().Be("Changed", "operation: AuditHistory.Operation.Modified");
        cells[3].Should().Be("Name", "field: the property name when no PropertyLabel is set");
        cells[4].Should().Be("Alpha");
        cells[5].Should().Be("Beta");
    }

    [Fact]
    public void ColumnHeaders_UseTheSharedLabelsInContractOrder()
    {
        var cut = RenderPanel(Serve([Entry(0)]));

        cut.WaitForAssertion(() => cut.FindAll("thead th").Should().HaveCount(6));
        cut.FindAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("When", "Changed by", "Change", "Field", "Before", "After");
    }

    [Theory]
    [InlineData("Added", "Created")]
    [InlineData("Modified", "Changed")]
    [InlineData("Deleted", "Deleted")]
    public void Operation_RendersItsSharedLabel(string operation, string expected)
    {
        var cut = RenderPanel(Serve([Entry(0, operation: operation)]));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        Cells(cut.Find("tbody tr"))[2].Should().Be(expected);
    }

    [Fact]
    public void SummaryRow_RendersEmptyFieldBeforeAndAfter()
    {
        var cut = RenderPanel(Serve([Entry(0, operation: "Added", property: null, oldValue: null, newValue: null)]));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        var cells = Cells(cut.Find("tbody tr"));
        cells[3].Should().BeEmpty();
        cells[4].Should().BeEmpty();
        cells[5].Should().BeEmpty();
    }

    [Fact]
    public void NullChangedBy_RendersSystemLabel()
    {
        var cut = RenderPanel(Serve([Entry(0, changedBy: null)]));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        Cells(cut.Find("tbody tr"))[1].Should().Be("System");
    }

    [Fact]
    public void UserLabel_AndPropertyLabel_AreApplied()
    {
        var cut = RenderPanel(
            Serve([Entry(0, changedBy: 42, property: "UnitPrice")]),
            p => p
                .Add(c => c.UserLabel, id => string.Create(CultureInfo.InvariantCulture, $"User number {id}"))
                .Add(c => c.PropertyLabel, name => name == "UnitPrice" ? "Unit price" : name));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        var cells = Cells(cut.Find("tbody tr"));
        cells[1].Should().Be("User number 42");
        cells[3].Should().Be("Unit price");
    }

    [Fact]
    public void ChangedOn_RenderedInDisplayTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Probe/PlusTwo", TimeSpan.FromHours(2), "Probe Plus Two", "Probe Standard Time");
        var expected = TimeZoneInfo.ConvertTimeFromUtc(BaseInstant, zone).ToString("g", CultureInfo.CurrentCulture);

        var cut = RenderPanel(Serve([Entry(0)]), p => p.Add(c => c.DisplayTimeZone, zone));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        var when = Cells(cut.Find("tbody tr"))[0];
        when.Should().Contain(expected, "the UTC instant is converted to DisplayTimeZone and formatted \"g\" in the current culture");
        when.Should().Contain("Probe Standard Time", "the zone's standard name is appended");
        when.Should().NotContain(BaseInstant.ToString("g", CultureInfo.CurrentCulture), "the UTC rendering is not shown");
    }

    [Fact]
    public void NullDisplayTimeZone_RendersUtc()
    {
        var cut = RenderPanel(Serve([Entry(0)]));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        var when = Cells(cut.Find("tbody tr"))[0];
        when.Should().Contain(BaseInstant.ToString("g", CultureInfo.CurrentCulture));
        when.Should().Contain(TimeZoneInfo.Utc.StandardName);
    }

    [Fact]
    public void Title_DefaultsToChangeHistory_AndCanBeOverridden()
    {
        var byDefault = RenderPanel(Serve([Entry(0)]));
        byDefault.Markup.Should().Contain("Change history");

        var custom = RenderUnderTest<AuditHistoryPanel>(p => p
            .Add(c => c.FetchPage, Serve([Entry(0)]))
            .Add(c => c.Title, "Product history"));
        custom.Markup.Should().Contain("Product history").And.NotContain("Change history");
    }

    // Paging (rule 7)
    [Fact]
    public async Task FullPage_ShowsLoadMore_ClickAppendsPageTwo()
    {
        var cut = RenderPanel(
            Serve([Entry(1, oldValue: "p1-a"), Entry(2, oldValue: "p1-b")], [Entry(3, oldValue: "p2-a")]),
            p => p.Add(c => c.PageSize, 2));
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2), TimeSpan.FromSeconds(5));

        await cut.FindButtonByText("Load more").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(3), TimeSpan.FromSeconds(5));
        cut.FindAll("tbody tr").Select(r => Cells(r)[4]).Should().Equal("p1-a", "p1-b", "p2-a");
        _calls.Should().Equal((1, 2), (2, 2));
        LoadMoreButtons(cut).Should().BeEmpty("the second page came back short");
    }

    [Fact]
    public void ShortPage_HidesLoadMore()
    {
        var cut = RenderPanel(Serve([Entry(1), Entry(2)]), p => p.Add(c => c.PageSize, 3));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        LoadMoreButtons(cut).Should().BeEmpty();
    }

    // Empty and failure states (rules 8, 9)
    [Fact]
    public void EmptyFirstPage_ShowsEmptyState()
    {
        var cut = RenderPanel(Serve([]));

        cut.WaitForAssertion(() => cut.FindComponents<EmptyState>().Should().ContainSingle());
        cut.FindComponent<EmptyState>().Instance.Message.Should().Be("No recorded changes.");
        cut.Markup.Should().Contain("No recorded changes.");
        cut.FindAll("tbody tr").Should().BeEmpty();
        LoadMoreButtons(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task FailedFirstPage_ShowsRetry_RetryRefetchesPageOne()
    {
        var attempts = 0;
        var cut = RenderPanel((page, size, _) =>
        {
            _calls.Add((page, size));
            return Task.FromResult(++attempts == 1
                ? Result.Failure<IReadOnlyList<AuditHistoryEntryDTO>>(Error.Unexpected("Http.500", "Server error"))
                : Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(1, oldValue: "recovered")]));
        });
        await cut.WaitForAssertionAsync(() => cut.FindComponents<ListNoRecordsContent>().Should().ContainSingle(), TimeSpan.FromSeconds(5));
        cut.FindComponent<ListNoRecordsContent>().Instance.LoadFailed.Should().BeTrue();

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        Cells(cut.Find("tbody tr"))[4].Should().Be("recovered");
        _calls.Should().Equal((1, 20), (1, 20));
    }

    [Fact]
    public async Task FailedLaterPage_KeepsRows_ShowsRetry()
    {
        var pageTwoAttempts = 0;
        var cut = RenderPanel(
            (page, size, _) =>
            {
                _calls.Add((page, size));
                if (page == 1)
                {
                    return Task.FromResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(1, oldValue: "p1-a"), Entry(2, oldValue: "p1-b")]));
                }

                return Task.FromResult(++pageTwoAttempts == 1
                    ? Result.Failure<IReadOnlyList<AuditHistoryEntryDTO>>(Error.Unexpected("Http.500", "Server error"))
                    : Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(3, oldValue: "p2-a")]));
            },
            p => p.Add(c => c.PageSize, 2));
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2), TimeSpan.FromSeconds(5));

        await cut.FindButtonByText("Load more").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindComponents<ListNoRecordsContent>().Should().ContainSingle(), TimeSpan.FromSeconds(5));
        cut.FindComponent<ListNoRecordsContent>().Instance.LoadFailed.Should().BeTrue();
        cut.FindAll("tbody tr").Should().HaveCount(2, "a failed later page keeps the rows already shown");

        await cut.FindButtonByText("Retry").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(3), TimeSpan.FromSeconds(5));
        _calls.Should().Equal((1, 2), (2, 2), (2, 2));
    }

    // Reload, supersession, dispose (rules 5, 10)
    [Fact]
    public async Task ReloadAsync_DiscardsSupersededResponse()
    {
        var stale = new TaskCompletionSource<Result<IReadOnlyList<AuditHistoryEntryDTO>>>();
        var tokens = new List<CancellationToken>();
        var cut = RenderPanel((page, size, ct) =>
        {
            _calls.Add((page, size));
            tokens.Add(ct);
            return tokens.Count == 1
                ? stale.Task
                : Task.FromResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(1, oldValue: "fresh")]));
        });
        await cut.WaitForAssertionAsync(() => tokens.Should().ContainSingle(), TimeSpan.FromSeconds(5));

        Task? reload = null;
        // A statement lambda: an expression lambda would bind InvokeAsync's Func<Task> overload.
        await cut.InvokeAsync(() => { reload = cut.Instance.ReloadAsync(); });
        await reload!.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        tokens[0].IsCancellationRequested.Should().BeTrue("ReloadAsync cancels the fetch it supersedes");
        stale.SetResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(2, oldValue: "stale")]));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("fresh"), TimeSpan.FromSeconds(5));
        cut.Markup.Should().NotContain("stale", "a response for a superseded fetch is discarded");
        cut.FindAll("tbody tr").Should().ContainSingle();
        _calls.Should().Equal((1, 20), (1, 20));
    }

    [Fact]
    public async Task ReloadAsync_ClearsRowsAndFetchesPageOneAgain()
    {
        var round = 0;
        var cut = RenderPanel(
            (page, size, _) =>
            {
                _calls.Add((page, size));
                var marker = string.Create(CultureInfo.InvariantCulture, $"round-{round}-page-{page}");
                return Task.FromResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(1, oldValue: marker), Entry(2, oldValue: marker + "-b")]));
            },
            p => p.Add(c => c.PageSize, 2));
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2), TimeSpan.FromSeconds(5));
        await cut.FindButtonByText("Load more").ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(4), TimeSpan.FromSeconds(5));
        round = 1;

        Task? reload = null;
        await cut.InvokeAsync(() => { reload = cut.Instance.ReloadAsync(); });
        await reload!.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2), TimeSpan.FromSeconds(5));
        cut.Markup.Should().Contain("round-1-page-1").And.NotContain("round-0-");
        _calls[^1].Should().Be((1, 2));
    }

    [Fact]
    public async Task Dispose_CancelsInFlightFetch()
    {
        var pending = new TaskCompletionSource<Result<IReadOnlyList<AuditHistoryEntryDTO>>>();
        CancellationToken seen = default;
        var cut = RenderPanel((page, size, ct) =>
        {
            _calls.Add((page, size));
            seen = ct;
            return pending.Task;
        });
        await cut.WaitForAssertionAsync(() => _calls.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        seen.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        seen.IsCancellationRequested.Should().BeTrue("the fetch token is cancelled when the panel is disposed");
        pending.TrySetCanceled(Ct);
    }

    // Loading indicator (rule 11)
    [Fact]
    public async Task WhileAFetchRuns_ProgressRendersInAStatusRegion()
    {
        var pending = new TaskCompletionSource<Result<IReadOnlyList<AuditHistoryEntryDTO>>>();
        var cut = RenderPanel((page, size, _) =>
        {
            _calls.Add((page, size));
            return pending.Task;
        });

        await cut.WaitForAssertionAsync(() => cut.FindComponents<MudProgressLinear>().Should().ContainSingle(), TimeSpan.FromSeconds(5));
        var status = cut.Find("[role=status]");
        status.QuerySelector("[aria-label='Loading']").Should().NotBeNull("the progress bar carries the Loading.Label aria-label inside role=status");
        status.QuerySelector(".mud-progress-linear").Should().NotBeNull();

        await cut.InvokeAsync(() => pending.SetResult(Result.Success<IReadOnlyList<AuditHistoryEntryDTO>>([Entry(1)])));

        await cut.WaitForAssertionAsync(() => cut.FindComponents<MudProgressLinear>().Should().BeEmpty(), TimeSpan.FromSeconds(5));
    }

    private static AuditHistoryEntryDTO Entry(
        int minutesBeforeBase,
        string operation = "Modified",
        UserIdentifierType? changedBy = 42,
        string? property = "Name",
        string? oldValue = "old",
        string? newValue = "new") => new()
        {
            Id = Guid.NewGuid(),
            EntityType = "MMCA.Store.Catalog.Domain.Products.Product",
            EntityKey = "17",
            PropertyName = property,
            OldValue = oldValue,
            NewValue = newValue,
            Operation = operation,
            ChangedBy = changedBy,
            ChangedOn = BaseInstant.AddMinutes(-minutesBeforeBase),
        };

    private static List<string> Cells(AngleSharp.Dom.IElement row) =>
        [.. row.QuerySelectorAll("td").Select(c => c.TextContent.Trim())];

    private static List<AngleSharp.Dom.IElement> LoadMoreButtons(IRenderedComponent<AuditHistoryPanel> cut) =>
        [.. cut.FindAll("button").Where(b => b.TextContent.Contains("Load more", StringComparison.Ordinal))];

    /// <summary>A fetcher answering page 1 with the supplied list (empty past the end), recording each call.</summary>
    private Func<int, int, CancellationToken, Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>>> Serve(
        IReadOnlyList<AuditHistoryEntryDTO> firstPage) =>
        ServePages([firstPage]);

    /// <summary>A fetcher answering pages 1 and 2 with the supplied lists (empty past the end), recording each call.</summary>
    private Func<int, int, CancellationToken, Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>>> Serve(
        IReadOnlyList<AuditHistoryEntryDTO> firstPage,
        IReadOnlyList<AuditHistoryEntryDTO> secondPage) =>
        ServePages([firstPage, secondPage]);

    /// <summary>A fetcher answering page N with the Nth supplied list (empty past the end), recording each call.</summary>
    private Func<int, int, CancellationToken, Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>>> ServePages(
        IReadOnlyList<AuditHistoryEntryDTO>[] pages) =>
        (page, size, _) =>
        {
            _calls.Add((page, size));
            IReadOnlyList<AuditHistoryEntryDTO> entries = page <= pages.Length ? pages[page - 1] : [];
            return Task.FromResult(Result.Success(entries));
        };

    private void Interactive() => SetRendererInfo(new RendererInfo("Server", isInteractive: true));

    private IRenderedComponent<AuditHistoryPanel> RenderPanel(
        Func<int, int, CancellationToken, Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>>> fetch,
        Action<ComponentParameterCollectionBuilder<AuditHistoryPanel>>? configure = null)
    {
        // After every Services.Add call: SetRendererInfo freezes the service provider.
        Interactive();
        return RenderUnderTest<AuditHistoryPanel>(p =>
        {
            p.Add(c => c.FetchPage, fetch);
            configure?.Invoke(p);
        });
    }
}
