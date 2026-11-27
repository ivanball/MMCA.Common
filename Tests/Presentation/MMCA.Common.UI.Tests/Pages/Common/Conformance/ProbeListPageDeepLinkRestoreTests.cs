using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// A deep link to a later page of a list (<c>/probes?p=1&amp;ps=5</c>, the second page of 36 rows) rendered
/// through the real <c>DataGridListPageBase</c>, <c>ListPageShell</c> and <c>MudDataGridPager</c>, with the
/// first-render restore held on its sessionStorage hydrate (a JS round trip that is slow on a real device).
/// Pins what the user sees: the pager range always matches the rows on screen while the restore is
/// pending (Store Run 5 R4), and a paging choice the user makes during that window wins over the restore
/// (Store Run 5 R6c).
/// </summary>
public sealed class ProbeListPageDeepLinkRestoreTests : BunitTestBase
{
    private const string NavInteropModulePath = "./_content/MMCA.Common.UI/nav-interop.js";
    private const string DeepLink = "/probes?p=1&ps=5";
    private const int Total = 36;
    private const string FirstPageRange = "1-5 of 36";
    private const string SecondPageRange = "6-10 of 36";

    private readonly Mock<IProbeRowClient> _client = new();
    private TaskCompletionSource? _fetchGate;

    public ProbeListPageDeepLinkRestoreTests()
    {
        Services.AddSingleton(_client.Object);
        _client
            .Setup(c => c.GetPagedAsync(
                It.IsAny<Dictionary<string, (string Operator, string Value)>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((Dictionary<string, (string Operator, string Value)> _, int pageNumber, int pageSize, string? _, string? _, CancellationToken _) =>
                AnswerAsync(pageNumber, pageSize));

        ConfigureDataGridListPageHost();
    }

    private static IEnumerable<string> SecondPageRows => Enumerable.Range(6, 5).Select(RowName);

    private static IEnumerable<string> RowsBeyondTheFirstPage => Enumerable.Range(6, Total - 5).Select(RowName);

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    // -- R4: the pager range agrees with the rows on screen during a deep link restore --
    [Fact]
    public async Task DeepLinkRestore_RowsArriveBeforeTheRestore_EveryRenderOfTheRestoredRowsShowsTheirRange()
    {
        _fetchGate = new TaskCompletionSource();
        Navigation.NavigateTo(DeepLink);
        var hydrate = HoldTheSessionHydrate();
        var cut = RenderPage();
        var renders = RecordRenders(cut);

        await cut.InvokeAsync(() => _fetchGate.SetResult());
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RowName(6)));
        await cut.InvokeAsync(() => { hydrate.SetResult(null); });
        await WaitForTheRestoreAsync(cut);

        cut.Markup.Should().Contain(RowName(6)).And.Contain(SecondPageRange);
        AssertEveryRenderAgreesWithItsRows(renders);
    }

    [Fact]
    public async Task DeepLinkRestore_RestoreCompletesBeforeTheRows_EveryRenderOfTheRestoredRowsShowsTheirRange()
    {
        _fetchGate = new TaskCompletionSource();
        Navigation.NavigateTo(DeepLink);
        var hydrate = HoldTheSessionHydrate();
        var cut = RenderPage();
        var renders = RecordRenders(cut);

        await cut.InvokeAsync(() => { hydrate.SetResult(null); });
        await WaitForTheRestoreAsync(cut);
        await cut.InvokeAsync(() => _fetchGate.SetResult());
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RowName(6)).And.Contain(SecondPageRange));
        await FlushTheDispatcherAsync(cut);

        AssertEveryRenderAgreesWithItsRows(renders);
    }

    // -- R6c: a paging choice made while the restore is pending wins over it --
    [Fact]
    public async Task DeepLink_FirstPageChosenBeforeTheRestoreCompletes_EndsOnTheFirstPage_AndTheUrlAgrees()
    {
        Navigation.NavigateTo(DeepLink);
        var hydrate = HoldTheSessionHydrate();
        var cut = RenderPage();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RowName(6)).And.Contain(SecondPageRange));

        await cut.Find("button[aria-label='First page']").ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RowName(1)).And.Contain(FirstPageRange));
        await cut.InvokeAsync(() => { hydrate.SetResult(null); });
        await WaitForTheRestoreAsync(cut);

        cut.Markup.Should().Contain(RowName(1)).And.Contain(FirstPageRange);
        cut.Markup.Should().NotContain(RowName(6), "the restore must not take the user back to the deep-linked page");
        (QueryValue(Navigation.Uri, "p") ?? "0").Should().Be("0", "the URL must name the first page too");
        QueryValue(Navigation.Uri, "ps").Should().Be("5");
    }

    [Fact]
    public async Task DeepLink_WithoutAPagingChoice_TheRestoreStillLandsOnTheDeepLinkedPage()
    {
        Navigation.NavigateTo(DeepLink);
        var hydrate = HoldTheSessionHydrate();
        var cut = RenderPage();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain(RowName(6)));

        await cut.InvokeAsync(() => { hydrate.SetResult(null); });
        await WaitForTheRestoreAsync(cut);

        cut.Markup.Should().Contain(RowName(6)).And.Contain(SecondPageRange);
        cut.Markup.Should().NotContain(RowName(1));
        QueryValue(Navigation.Uri, "p").Should().Be("1");
        QueryValue(Navigation.Uri, "ps").Should().Be("5");
    }

    private static string RowName(int n) => string.Create(CultureInfo.InvariantCulture, $"Row {n:D2}");

    private static void AssertEveryRenderAgreesWithItsRows(List<string> renders)
    {
        var withRestoredRows = renders.Where(m => SecondPageRows.Any(row => m.Contains(row, StringComparison.Ordinal))).ToList();
        withRestoredRows.Should().NotBeEmpty("the restored page's rows must render at all");
        withRestoredRows.Should().OnlyContain(
            m => m.Contains(SecondPageRange, StringComparison.Ordinal),
            "a render that shows the second page's rows must show the second page's range");
        renders
            .Where(m => m.Contains(FirstPageRange, StringComparison.Ordinal))
            .Should().OnlyContain(
                m => !RowsBeyondTheFirstPage.Any(row => m.Contains(row, StringComparison.Ordinal)),
                "no render may show the first page's range over other rows");
    }

    private static string? QueryValue(string uri, string key)
    {
        var query = new Uri(uri).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
            {
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
        }

        return null;
    }

    private static List<string> RecordRenders(IRenderedComponent<RestoreObservingProbeListPage> cut)
    {
        var renders = new List<string> { cut.Markup };
        cut.OnMarkupUpdated += (_, _) => renders.Add(cut.Markup);
        return renders;
    }

    // The restore ends without a render of its own, so it is awaited rather than polled on renders; the
    // bound only turns a restore that never completes into a failure instead of a hung run.
    private static async Task WaitForTheRestoreAsync(IRenderedComponent<RestoreObservingProbeListPage> cut)
    {
        await cut.Instance.RestoreApplied.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);
        await FlushTheDispatcherAsync(cut);
    }

    // Work the restore queued on the renderer (a reload, a re-render) runs before a later InvokeAsync does.
    private static Task FlushTheDispatcherAsync(IRenderedComponent<RestoreObservingProbeListPage> cut) =>
        cut.InvokeAsync(() => { });

    private async Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>> AnswerAsync(int pageNumber, int pageSize)
    {
        if (_fetchGate is { } gate)
        {
            await gate.Task;
        }

        var first = (pageNumber - 1) * pageSize + 1;
        var count = Math.Max(0, Math.Min(pageSize, Total - first + 1));
        IReadOnlyList<ProbeRow> rows = [.. Enumerable.Range(first, count).Select(n => new ProbeRow(n, RowName(n)))];
        return Result.Success<(IReadOnlyList<ProbeRow> Items, int TotalItems)>((rows, Total));
    }

    private JSRuntimeInvocationHandler<ListPageState?> HoldTheSessionHydrate() =>
        JSInterop.SetupModule(NavInteropModulePath).Setup<ListPageState?>("sessionGet", _ => true);

    private IRenderedComponent<RestoreObservingProbeListPage> RenderPage()
    {
        RenderMudProviders();
        return RenderUnderTest<RestoreObservingProbeListPage>(_ => { });
    }

    /// <summary>The probe page, reporting when the first-render restore has been applied.</summary>
    public sealed class RestoreObservingProbeListPage : ProbeListPage
    {
        private readonly TaskCompletionSource _restoreApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RestoreApplied => _restoreApplied.Task;

        protected override Task OnRestoredStateAppliedAsync(bool needsReload)
        {
            _restoreApplied.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
